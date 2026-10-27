using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace TagsTool;

/// <summary>
/// Этап 2: гоняет каждый отмеченный keep тег через POST /v1/Tags/completion,
/// то есть через твой GetTagCompletionCommand с его промптом. В базу ничего
/// не пишет — результат складывается в JSON на вычитку.
///
/// Перезапускаемо: уже переведённые теги пропускаются.
/// Параллельно: число одновременных запросов задаётся через --threads.
/// </summary>
internal static class TranslateStage
{
	/// <summary>Максимум для --threads.</summary>
	public const int MaxThreads = 2500;

	public static async Task RunAsync(
		BackendApi api,
		string csvPath,
		string outputPath,
		int delayMs,
		int threads)
	{
		await api.EnsureReachableAsync();

		// Слияние происходит здесь: две строки с одинаковым slug — один тег.
		var rows = CsvFile.Read(csvPath)
			.Where(r => r.Action.Equals("keep", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var prepared = rows
			.GroupBy(r => r.Slug, StringComparer.OrdinalIgnoreCase)
			.Select(g =>
			{
				var main = g.OrderByDescending(r => r.Count).First();

				return new TranslatedTag
				{
					Slug = g.Key,
					Label = main.Label,
					Count = g.Sum(r => r.Count),
					SourceTags = g.SelectMany(r => r.SourceTags).Distinct().ToList(),
					Translations = [],
					Aliases = []
				};
			})
			.OrderByDescending(t => t.Count)
			.ToList();

		Console.WriteLine($"Строк keep: {rows.Count}, после слияния по slug: {prepared.Count}");

		// Возобновление: каждый тег — вызов к DeepSeek, терять сделанное обидно.
		// ConcurrentDictionary, потому что дописывают несколько потоков сразу.
		var done = new ConcurrentDictionary<string, TranslatedTag>(StringComparer.OrdinalIgnoreCase);

		if (File.Exists(outputPath))
		{
			var existing = JsonSerializer.Deserialize<List<TranslatedTag>>(
				await File.ReadAllTextAsync(outputPath), BackendApi.Json) ?? [];

			foreach (var tag in existing.Where(t => t.Translations.Count >= 3))
				done[tag.Slug] = tag;

			Console.WriteLine($"Найден {Path.GetFileName(outputPath)}, уже переведено: {done.Count}");
		}

		var pending = prepared.Where(t => !done.ContainsKey(t.Slug)).ToList();

		if (pending.Count == 0)
		{
			Console.WriteLine("Переводить нечего — всё уже сделано.");
			return;
		}

		Console.WriteLine($"К переводу: {pending.Count}, потоков: {threads}");

		if (threads > 32)
			Console.WriteLine(
				"ВНИМАНИЕ: каждый поток — это запрос к твоему серверу, который дёргает DeepSeek. " +
				"При большом числе потоков упрёшься в рейт-лимит провайдера или в пул соединений " +
				"сервера, и часть тегов вернётся с ошибкой. Их всегда можно добрать повторным запуском.");

		// -- Чекпоинт ---------------------------------------------------------
		// Сохраняем по ходу дела, но склеиваем одновременные попытки: если запись
		// уже идёт, остальные потоки её пропускают. Иначе при 2500 потоках файл
		// переписывался бы тысячи раз подряд без всякой пользы.
		// Полнота гарантируется финальным сохранением ниже.
		int savePending = 0;

		async Task CheckpointAsync()
		{
			if (Interlocked.CompareExchange(ref savePending, 1, 0) != 0)
				return;

			try
			{
				await SaveAsync(outputPath, prepared, done);
			}
			finally
			{
				Interlocked.Exchange(ref savePending, 0);
			}
		}

		// -- Ctrl+C -----------------------------------------------------------
		// Перехватываем, чтобы успеть дописать файл: при параллельной работе
		// в воздухе висит до threads уже оплаченных ответов, терять их незачем.
		using var cts = new CancellationTokenSource();

		ConsoleCancelEventHandler onCancel = (_, e) =>
		{
			e.Cancel = true;            // не даём среде убить процесс сразу
			Console.WriteLine("\nПрерывание: доделываю текущие запросы и сохраняю...");
			cts.Cancel();
		};

		Console.CancelKeyPress += onCancel;

		int completed = 0;
		int failed = 0;

		try
		{
			await Parallel.ForEachAsync(
				pending,
				new ParallelOptions
				{
					MaxDegreeOfParallelism = threads,
					CancellationToken = cts.Token
				},
				async (tag, token) =>
				{
					try
					{
						var completion = await api.CompleteTagAsync(tag.Label, token);

						// Slug берём СВОЙ, из CSV: он прошёл твою проверку и служит
						// ключом слияния. Тот, что вернул сервер, игнорируем.
						tag.Translations = completion.Translations ?? [];
						tag.Aliases = completion.Aliases ?? [];

						int n = Interlocked.Increment(ref completed);
						Console.WriteLine($"[{n}/{pending.Count}] {tag.Label} -> " +
							string.Join(" / ", tag.Translations.Select(t => t.Text)));
					}
					catch (OperationCanceledException)
					{
						throw;   // прерывание — наверх, к Parallel.ForEachAsync
					}
					catch (Exception ex)
					{
						// Не роняем прогон из-за одного тега: пишем с пустым
						// переводом, доправишь руками либо доберёшь перезапуском.
						Interlocked.Increment(ref failed);
						int n = Interlocked.Increment(ref completed);
						Console.WriteLine($"[{n}/{pending.Count}] ОШИБКА '{tag.Label}': {ex.Message}");
					}

					done[tag.Slug] = tag;

					await CheckpointAsync();

					// При threads > 1 пауза действует на каждый поток отдельно,
					// то есть работает как простой троттлинг.
					if (delayMs > 0)
						await Task.Delay(delayMs, token);
				});
		}
		catch (OperationCanceledException)
		{
			Console.WriteLine("Прервано пользователем.");
		}
		finally
		{
			Console.CancelKeyPress -= onCancel;

			// Финальное сохранение делаем всегда, в том числе после прерывания
			// и после исключения: чекпоинты могли пропустить последние ответы.
			await SaveAsync(outputPath, prepared, done);
		}

		int incomplete = done.Values.Count(t => t.Translations.Count < 3);

		Console.WriteLine($"""

			Записано: {Path.GetFullPath(outputPath)}
			Тегов: {done.Count}, ошибок в этот прогон: {failed}, без полного перевода: {incomplete}

			Дальше вычитай файл. Чаще всего модель ошибается в японском (катакана
			там, где нужен кандзи) и в алиасах (лишние или слишком широкие).
			Теги без трёх переводов импорт не пропустит — доправь или удали.
			Повторный запуск доберёт только недоделанное.
			""");
	}

	/// <summary>
	/// Порядок в файле всегда как в prepared (по убыванию частоты), независимо
	/// от того, в каком порядке потоки завершились. Иначе diff между прогонами
	/// было бы невозможно читать.
	/// </summary>
	private static async Task SaveAsync(
		string path,
		List<TranslatedTag> order,
		ConcurrentDictionary<string, TranslatedTag> done)
	{
		var sequence = order
			.Where(o => done.ContainsKey(o.Slug))
			.Select(o => done[o.Slug])
			.ToList();

		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(sequence, BackendApi.Json),
			new UTF8Encoding(false));
	}
}

/// <summary>Итог второго этапа — то, что ты вычитываешь перед импортом.</summary>
internal sealed class TranslatedTag
{
	public required string Slug { get; set; }

	/// <summary>Исходная английская форма — чтобы было понятно, что это за тег.</summary>
	public required string Label { get; set; }

	/// <summary>Сколько фотографий получит тег при --assign.</summary>
	public required int Count { get; set; }

	/// <summary>Исходные строки из sqlite. Нужны для расстановки, не удалять.</summary>
	public required List<string> SourceTags { get; set; }

	/// <summary>Три основные подписи (IsPrimary = true на стороне сервера).</summary>
	public required List<TagText> Translations { get; set; }

	/// <summary>Синонимы для поиска (IsPrimary = false).</summary>
	public required List<TagText> Aliases { get; set; }
}
