using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TagsTool;

/// <summary>
/// Этап 3: создаёт теги через POST /v1/Tags и, по флагу --assign, расставляет
/// их фотографиям через PATCH /v1/Media.
///
/// Своего подключения к Postgres нет: всё пишет сервер своими же командами,
/// со своей валидацией и своими правилами.
/// </summary>
internal static class ImportStage
{
	public static async Task RunAsync(
		BackendApi api,
		string jsonPath,
		string? sqlitePath,
		bool assignToMedia)
	{
		await api.EnsureReachableAsync();

		if (!File.Exists(jsonPath))
			throw new FileNotFoundException($"Не найден файл {Path.GetFullPath(jsonPath)}");

		var tags = JsonSerializer.Deserialize<List<TranslatedTag>>(
			await File.ReadAllTextAsync(jsonPath), BackendApi.Json) ?? [];

		var invalid = tags.Where(t => t.Translations.Count < 3).ToList();
		if (invalid.Count > 0)
		{
			Console.WriteLine($"Отказ: у {invalid.Count} тегов нет всех трёх переводов:");
			foreach (var tag in invalid.Take(10))
				Console.WriteLine($"  {tag.Slug}");

			Console.WriteLine("Доправь их в JSON или удали из файла.");
			return;
		}

		// -- Создание словаря -------------------------------------------------
		var existing = await api.GetTagsAsync();
		var idBySlug = existing.ToDictionary(t => t.Slug, t => t.Id, StringComparer.OrdinalIgnoreCase);

		Console.WriteLine($"Уже в базе: {existing.Count} тегов");

		int created = 0, skipped = 0;

		foreach (var tag in tags)
		{
			if (idBySlug.ContainsKey(tag.Slug))
			{
				skipped++;
				continue;
			}

			// Алиас, совпавший с основной подписью или с другим алиасом,
			// нарушит уникальность в пределах тега — отсеиваем заранее.
			var seen = new HashSet<string>(
				tag.Translations.Select(t => t.Text.Trim()),
				StringComparer.OrdinalIgnoreCase);

			var aliases = new List<TagText>();
			foreach (var alias in tag.Aliases)
			{
				string text = alias.Text.Trim().ToLowerInvariant();
				if (!seen.Add(text))
					continue;

				aliases.Add(new TagText(alias.LanguageCode, text));
			}

			var translations = tag.Translations
				.GroupBy(t => t.LanguageCode, StringComparer.OrdinalIgnoreCase)
				.Select(g => new TagText(g.Key, g.First().Text.Trim()))
				.ToList();

			try
			{
				int id = await api.AddTagAsync(tag.Slug, translations, aliases);
				idBySlug[tag.Slug] = id;
				created++;

				Console.WriteLine($"[{created}] {tag.Slug} -> id {id}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"НЕ СОЗДАН {tag.Slug}: {ex.Message}");
			}
		}

		Console.WriteLine($"Создано: {created}, пропущено (уже были): {skipped}");

		if (!assignToMedia)
		{
			Console.WriteLine("""

				Расстановка по фотографиям пропущена (нет --assign).
				Проверь словарь в админке, и если всё устраивает — запусти ещё раз
				с --assign --sqlite photos.sqlite.
				""");
			return;
		}

		if (sqlitePath is null)
			throw new ArgumentException("Для --assign нужен --sqlite");

		await AssignAsync(api, sqlitePath, tags, idBySlug);
	}

	// ------------------------------------------------------------------------

	private static async Task AssignAsync(
		BackendApi api,
		string sqlitePath,
		List<TranslatedTag> tags,
		Dictionary<string, int> idBySlug)
	{
		// Карта: исходная строка тега из sqlite -> id тега в базе.
		// Здесь схлопывается слияние: 'city', 'cityscape' и 'urban' указывают
		// на один и тот же id.
		var tagIdByRawTag = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var tag in tags)
		{
			if (!idBySlug.TryGetValue(tag.Slug, out int id))
				continue;

			foreach (var raw in tag.SourceTags)
				tagIdByRawTag[raw] = id;
		}

		// Считаем, какие теги нужны какому файлу. Ключ — имя файла БЕЗ
		// расширения: при конвертации меняется только оно, само имя остаётся.
		var tagIdsByFileName = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);

		await using (var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly"))
		{
			await sqlite.OpenAsync();

			var command = sqlite.CreateCommand();
			command.CommandText = """
				SELECT i.filename, v.tags_json
				FROM images i JOIN vlm v ON v.image_id = i.id
				WHERE v.tags_json IS NOT NULL
				""";

			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				string key = Path.GetFileNameWithoutExtension(reader.GetString(0));
				var rawTags = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];

				foreach (var raw in rawTags)
				{
					if (!tagIdByRawTag.TryGetValue(raw, out int id))
						continue;   // тег отброшен на этапе 1 — так и задумано

					if (!tagIdsByFileName.TryGetValue(key, out var set))
						tagIdsByFileName[key] = set = [];

					set.Add(id);
				}
			}
		}

		Console.WriteLine($"Файлов с тегами в sqlite: {tagIdsByFileName.Count}");

		// -- Сопоставление с медиафайлами сервера -----------------------------
		//
		// ВАЖНО: EditMediaCommand принимает Ids медиафайлов, а не имена. Чтобы
		// не заводить своё подключение к базе, сопоставление имя->Id берём из
		// файла-карты, который ты выгружаешь из админки или из БД одним
		// запросом. Формат простой: JSON вида { "PXL_20260321_074817458": 1500 }
		//
		//     psql -At -c "\copy (SELECT json_object_agg(
		//         regexp_replace(file_name, '\.[^.]+$', ''), id)
		//         FROM media_files WHERE type = 'Image') TO 'media-map.json'"
		//
		// Так инструмент остаётся без доступа к Postgres, а ты явно видишь,
		// на какие файлы он будет писать.
		const string MapFileName = "media-map.json";

		if (!File.Exists(MapFileName))
		{
			Console.WriteLine($"""

				Нет файла {MapFileName} — без него не сопоставить имена файлов с Id.

				Выгрузи карту одним запросом к своей базе:

				  SELECT json_object_agg(regexp_replace(file_name, '\.[^.]+$', ''), id)
				  FROM media_files
				  WHERE type = 'Image';

				Сохрани результат как {MapFileName} рядом с инструментом и повтори.
				""");
			return;
		}

		var mediaIdByName = JsonSerializer.Deserialize<Dictionary<string, int>>(
			await File.ReadAllTextAsync(MapFileName),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

		Console.WriteLine($"В карте {MapFileName}: {mediaIdByName.Count} файлов");

		// -- Группируем по одинаковому набору тегов ---------------------------
		// EditMediaCommand принимает список Ids и общий набор изменений,
		// поэтому файлы с одинаковым набором тегов можно отправить одним
		// запросом. Это сокращает ~4200 запросов до нескольких сотен.
		var batches = new Dictionary<string, (List<int> MediaIds, List<int> TagIds)>();

		int unmatched = 0;

		foreach (var (fileName, tagIds) in tagIdsByFileName)
		{
			if (!mediaIdByName.TryGetValue(fileName, out int mediaId))
			{
				unmatched++;
				continue;
			}

			string key = string.Join(',', tagIds);

			if (!batches.TryGetValue(key, out var batch))
				batches[key] = batch = ([], tagIds.ToList());

			batch.MediaIds.Add(mediaId);
		}

		Console.WriteLine($"Файлов без пары в карте: {unmatched}");
		Console.WriteLine($"Запросов к серверу: {batches.Count}");

		int done = 0, assigned = 0;

		foreach (var (_, batch) in batches)
		{
			try
			{
				await api.AssignTagsAsync(batch.MediaIds, batch.TagIds);
				assigned += batch.MediaIds.Count;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"ОШИБКА на пачке из {batch.MediaIds.Count} файлов: {ex.Message}");
			}

			done++;
			if (done % 25 == 0)
				Console.WriteLine($"  {done}/{batches.Count} пачек");
		}

		Console.WriteLine($"""

			Проставлено тегов файлам: {assigned}

			ПОМНИ: это машинная разметка Qwen, а не твоя. Она годится, чтобы
			проверить поиск и фасеты на реальном объёме. Когда начнёшь размечать
			руками — сначала снеси импортированное, иначе через месяц не отличишь
			своё от машинного.
			""");
	}
}
