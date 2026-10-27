using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace TagsTool;

/// <summary>
/// Импорт векторов изображений из photos.sqlite в таблицу media_embeddings.
///
/// ОТЛИЧИЕ ОТ ОСТАЛЬНЫХ КОМАНД. Теги ходят через HTTP API сервера, потому что
/// там есть готовые команды (AddTagCommand, EditMediaCommand) со своей
/// валидацией. Для векторов такой логики на сервере нет и не будет — это
/// ответвление, а не часть приложения. Поэтому здесь единственный случай,
/// когда инструмент пишет в Postgres напрямую.
///
/// Риск от этого небольшой: media_embeddings — производная таблица, в ней нет
/// ничего, что нельзя пересобрать из sqlite за минуту. Ни одна таблица сервера
/// при импорте не изменяется, media_files только читается.
/// </summary>
internal static class EmbeddingsStage
{
	private const int Dim = 768;
	private const int VectorBytes = Dim * sizeof(float);

	public static async Task RunAsync(
		string sqlitePath,
		string? connectionString,
		string table,
		bool dryRun)
	{
		if (!File.Exists(sqlitePath))
			throw new FileNotFoundException($"Не найден файл {Path.GetFullPath(sqlitePath)}");

		// -- 1. Читаем векторы из sqlite -------------------------------------
		var vectors = await ReadSqliteAsync(sqlitePath);

		Console.WriteLine($"Векторов в sqlite: {vectors.Count}");

		if (vectors.Count == 0)
		{
			Console.WriteLine("Импортировать нечего.");
			return;
		}

		var models = vectors.Select(v => v.Model).Distinct().ToList();
		Console.WriteLine($"Модель: {string.Join(", ", models)}");

		if (models.Count > 1)
			throw new InvalidOperationException(
				"В sqlite векторы от разных моделей. Сравнивать их между собой нельзя — " +
				"это разные пространства. Импорт остановлен.");

		Console.WriteLine($"Объём: {vectors.Count * VectorBytes / 1024.0 / 1024.0:F1} МБ");

		// -- 2. Режим проверки: без Postgres ---------------------------------
		// Позволяет убедиться, что файл читается и формат тот, ещё до того как
		// вводить строку подключения.
		if (dryRun)
		{
			Console.WriteLine($"""

				--dry-run: к базе не подключались, ничего не записано.

				Проверено: формат {Dim} float32, длина блоба {VectorBytes} байт,
				нормализация в пределах допуска.

				Для настоящего импорта убери --dry-run и добавь --connection.
				""");
			return;
		}

		if (string.IsNullOrWhiteSpace(connectionString))
			throw new ArgumentException(
				"Нужен --connection со строкой подключения к Postgres " +
				"(или переменная TAGSTOOL_CONNECTION). " +
				"Пример: --connection \"Host=localhost;Database=haru;Username=postgres;Password=...\"");

		// -- 3. Подключаемся и сверяем с media_files -------------------------
		await using var db = new NpgsqlConnection(connectionString);

		try
		{
			await db.OpenAsync();
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"Не удалось подключиться к Postgres: {ex.Message}");
		}

		await EnsureTableExistsAsync(db, table);

		var mediaByName = await ReadMediaFilesAsync(db);
		Console.WriteLine($"Изображений в media_files: {mediaByName.Count}");

		// -- 4. Сопоставление по имени файла без расширения ------------------
		// При конвертации у тебя меняется только расширение, само имя остаётся.
		var rows = new List<(int MediaFileId, float[] Vector, string Model)>();
		var matchedMediaIds = new HashSet<int>();
		var unmatched = new List<string>();

		foreach (var item in vectors)
		{
			string key = Path.GetFileNameWithoutExtension(item.FileName).ToLowerInvariant();

			if (!mediaByName.TryGetValue(key, out int mediaFileId))
			{
				unmatched.Add(item.FileName);
				continue;
			}

			// Один медиафайл — один вектор. Дубли в sqlite игнорируем.
			if (!matchedMediaIds.Add(mediaFileId))
				continue;

			rows.Add((mediaFileId, item.Vector, item.Model));
		}

		int imagesWithoutVector = mediaByName.Count - matchedMediaIds.Count;

		Console.WriteLine($"Сопоставлено: {rows.Count}");
		Console.WriteLine($"Из sqlite без пары в media_files: {unmatched.Count}");
		Console.WriteLine($"Изображений в базе без вектора: {imagesWithoutVector}");

		if (unmatched.Count > 0)
		{
			Console.WriteLine("  примеры: " + string.Join(", ", unmatched.Take(5)));
		}

		if (rows.Count == 0)
		{
			Console.WriteLine("""

				Ни одно имя не совпало. Скорее всего media_files ещё не заполнен
				(не отработал SyncMediaCommand) либо имена файлов не сохранились
				при загрузке на файлсервер. Ничего не записано.
				""");
			return;
		}

		// -- 5. Запись в одной транзакции ------------------------------------
		// Идемпотентно: удаляем только те строки, которые сейчас перезапишем,
		// чужие не трогаем. Так повторный прогон безопасен.
		await using var transaction = await db.BeginTransactionAsync();

		var ids = rows.Select(r => r.MediaFileId).ToArray();

		await using (var delete = new NpgsqlCommand(
			$"DELETE FROM {table} WHERE media_file_id = ANY(@ids)", db, transaction))
		{
			delete.Parameters.AddWithValue("ids", ids);
			int removed = await delete.ExecuteNonQueryAsync();

			if (removed > 0)
				Console.WriteLine($"Перезаписываем существующих строк: {removed}");
		}

		// Бинарный COPY — самый быстрый способ залить 4200 строк по 3 КБ.
		await using (var writer = await db.BeginBinaryImportAsync(
			$"COPY {table} (media_file_id, vector, model) FROM STDIN (FORMAT BINARY)"))
		{
			foreach (var (mediaFileId, vector, model) in rows)
			{
				await writer.StartRowAsync();
				await writer.WriteAsync(mediaFileId, NpgsqlDbType.Integer);
				await writer.WriteAsync(vector, NpgsqlDbType.Array | NpgsqlDbType.Real);
				await writer.WriteAsync(model, NpgsqlDbType.Text);
			}

			await writer.CompleteAsync();
		}

		await transaction.CommitAsync();

		// -- 6. Проверка того, что реально легло ------------------------------
		await using (var check = new NpgsqlCommand(
			$"SELECT count(*), min(array_length(vector, 1)), max(array_length(vector, 1)) FROM {table}", db))
		await using (var reader = await check.ExecuteReaderAsync())
		{
			await reader.ReadAsync();
			Console.WriteLine($"""

				Готово. В таблице {table}: {reader.GetInt64(0)} строк,
				длина вектора от {reader.GetInt32(1)} до {reader.GetInt32(2)}.
				""");
		}

		if (imagesWithoutVector > 0)
		{
			Console.WriteLine($"""

				ВНИМАНИЕ: {imagesWithoutVector} изображений остались без вектора.
				Они никогда не появятся в подсказках по похожести, и заметить это
				самостоятельно почти невозможно. Стоит разобраться, почему их имена
				не совпали.
				""");
		}
	}

	// ------------------------------------------------------------------------

	private static async Task<List<(string FileName, float[] Vector, string Model)>> ReadSqliteAsync(string sqlitePath)
	{
		var result = new List<(string, float[], string)>();

		await using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
		await sqlite.OpenAsync();

		var command = sqlite.CreateCommand();
		command.CommandText = """
			SELECT i.filename, e.model, e.dim, e.vector
			FROM images i
			JOIN embeddings e ON e.image_id = i.id
			WHERE i.status_embed = 'done'
			ORDER BY i.id
			""";

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			string fileName = reader.GetString(0);
			string model = reader.GetString(1);
			int dim = reader.GetInt32(2);
			byte[] blob = (byte[])reader["vector"];

			// Защита от молчаливой порчи данных: лучше упасть на импорте, чем
			// потом искать причину странной выдачи.
			if (dim != Dim || blob.Length != VectorBytes)
				throw new InvalidOperationException(
					$"{fileName}: ожидался вектор {Dim} float32 ({VectorBytes} байт), " +
					$"получено dim={dim}, {blob.Length} байт");

			// BLOB — ровно 768 float32 little-endian подряд. На x86 и ARM
			// порядок байтов совпадает, конвертация не нужна.
			var vector = new float[Dim];
			Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);

			// Векторы должны приходить уже L2-нормализованными: на этом держится
			// то, что скалярное произведение равно косинусу.
			double norm = 0;
			foreach (float x in vector)
				norm += (double)x * x;

			norm = Math.Sqrt(norm);

			if (Math.Abs(norm - 1.0) > 0.01)
				throw new InvalidOperationException(
					$"{fileName}: вектор не нормализован (длина {norm:F4}, ожидалась 1.0). " +
					"Дальнейшие расчёты похожести были бы неверны.");

			result.Add((fileName, vector, model));
		}

		return result;
	}

	private static async Task<Dictionary<string, int>> ReadMediaFilesAsync(NpgsqlConnection db)
	{
		var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		// Тип хранится строкой (HasConversion<string>), поэтому сравниваем с 'Image'.
		await using var command = new NpgsqlCommand(
			"SELECT id, file_name FROM media_files WHERE type = 'Image'", db);

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			int id = reader.GetInt32(0);
			string key = Path.GetFileNameWithoutExtension(reader.GetString(1)).ToLowerInvariant();

			// Если после конвертации два файла дали одинаковое имя — берём первый
			// и не роняем импорт.
			result.TryAdd(key, id);
		}

		return result;
	}

	/// <summary>
	/// Проверяем таблицу заранее, с понятным сообщением: иначе человек получит
	/// сырую ошибку Postgres посреди импорта.
	/// </summary>
	private static async Task EnsureTableExistsAsync(NpgsqlConnection db, string table)
	{
		await using var command = new NpgsqlCommand(
			"SELECT to_regclass(@name) IS NOT NULL", db);

		command.Parameters.AddWithValue("name", table);

		bool exists = await command.ExecuteScalarAsync() is true;

		if (!exists)
			throw new InvalidOperationException(
				$"Таблицы '{table}' нет в базе. Сначала создай сущность MediaEmbedding " +
				"и выполни миграцию на сервере — см. README, раздел «Векторы».");
	}
}
