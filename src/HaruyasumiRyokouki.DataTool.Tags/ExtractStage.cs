using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TagsTool;

/// <summary>
/// Этап 1: читает теги из photos.sqlite, нормализует, склеивает очевидные дубли,
/// подсказывает неочевидные и пишет CSV на проверку.
///
/// Полностью офлайн: ни сервера, ни базы, ни LLM. Можно гонять сколько угодно.
/// </summary>
internal static class ExtractStage
{
	private const int Dim = 768;

	/// <summary>
	/// Теги, применимые примерно ко всему архиву и потому бесполезные как фильтр.
	/// Предзаполняются как drop, вернуть можно правкой ячейки в CSV.
	/// </summary>
	private static readonly HashSet<string> Stoplist = new(StringComparer.OrdinalIgnoreCase)
	{
		"japan", "japanese", "travel", "tourism", "outdoor", "indoor",
		"architecture", "building", "traditional", "serene",
		"scenic", "beautiful", "view", "photo", "image"
	};

	public static async Task RunAsync(string sqlitePath, string outputPath, int minCount)
	{
		if (!File.Exists(sqlitePath))
			throw new FileNotFoundException($"Не найден файл {Path.GetFullPath(sqlitePath)}");

		// -- 1. Все теги с их фотографиями -----------------------------------
		var photosByRawTag = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

		await using (var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly"))
		{
			await sqlite.OpenAsync();

			var command = sqlite.CreateCommand();
			command.CommandText = "SELECT image_id, tags_json FROM vlm WHERE tags_json IS NOT NULL";

			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				int imageId = reader.GetInt32(0);
				var tags = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];

				foreach (var tag in tags)
				{
					if (!photosByRawTag.TryGetValue(tag, out var set))
						photosByRawTag[tag] = set = [];
					set.Add(imageId);
				}
			}
		}

		Console.WriteLine($"Исходных тегов: {photosByRawTag.Count}");

		// -- 2. Нормализация: регистр, подчёркивания и дефисы в пробелы -------
		// Склеивает "cherry_blossoms" / "cherry blossoms" и "Japan" / "japan".
		var groups = new Dictionary<string, TagGroup>(StringComparer.Ordinal);

		foreach (var (raw, photos) in photosByRawTag)
		{
			string normalized = Normalize(raw);
			if (normalized.Length == 0)
				continue;

			if (!groups.TryGetValue(normalized, out var group))
				groups[normalized] = group = new TagGroup(normalized);

			group.RawForms.Add(raw);
			group.Photos.UnionWith(photos);
		}

		// -- 3. Единственное/множественное ------------------------------------
		// Склеиваем ТОЛЬКО если обе формы есть в данных: тогда mountain +
		// mountains сливаются, а одинокое "glasses" не калечится.
		foreach (var plural in groups.Keys.ToList())
		{
			string singular = Singularize(plural);
			if (singular == plural)
				continue;

			if (!groups.TryGetValue(singular, out var target) || !groups.TryGetValue(plural, out var source))
				continue;

			target.RawForms.UnionWith(source.RawForms);
			target.Photos.UnionWith(source.Photos);
			target.MergedNotes.Add($"{plural} ({source.Photos.Count})");
			groups.Remove(plural);
		}

		var all = groups.Values.OrderByDescending(g => g.Photos.Count).ToList();

		Console.WriteLine($"После нормализации и склейки форм: {all.Count}");

		// -- 4. Подсказка по дублям (по векторам, если они есть) -------------
		// Считаем только для достаточно частых: центроид по одной фотографии
		// это просто она сама, сравнивать такое бессмысленно.
		var hints = BuildDuplicateHints(all.Where(g => g.Photos.Count >= 5).ToList(), sqlitePath);

		// -- 5. CSV -----------------------------------------------------------
		// ВАЖНО: в файл попадают ВСЕ теги. minCount только предзаполняет
		// колонку action. Ни одна строка не теряется — редкий тег это то, что
		// делает находимой единственную фотографию вагю-бургера.
		var lines = new List<string>
		{
			"slug,label,count,band,action,duplicate_hint,merged_forms,source_tags"
		};

		int keepCount = 0;

		foreach (var group in all)
		{
			int count = group.Photos.Count;
			string band = count >= minCount ? "core" : count >= 2 ? "tail" : "single";
			bool drop = Stoplist.Contains(group.Normalized) || count < minCount;

			if (!drop)
				keepCount++;

			lines.Add(string.Join(',',
				Csv(Slugify(group.Normalized)),
				Csv(group.Normalized),
				count.ToString(CultureInfo.InvariantCulture),
				band,
				drop ? "drop" : "keep",
				Csv(hints.GetValueOrDefault(group.Normalized, "")),
				Csv(string.Join("; ", group.MergedNotes)),
				Csv(string.Join("|", group.RawForms))));
		}

		// BOM обязателен: без него Excel читает UTF-8 как ANSI и кириллица плывёт.
		await File.WriteAllLinesAsync(outputPath, lines, new UTF8Encoding(true));

		Console.WriteLine($"Предзаполнено keep: {keepCount}");
		Console.WriteLine($"""

			Записано: {Path.GetFullPath(outputPath)}

			Дальше открой файл в Excel и:
			  1. Правь колонку action (keep/drop). Каждый keep — один вызов DeepSeek.
			     Колонка band подсказывает: core (>= {minCount} фото), tail (2..{minCount - 1}), single (1).
			  2. Сливай дубли через slug: две строки с одинаковым slug станут одним
			     тегом, подпись возьмётся у строки с большим count.
			  3. Смотри duplicate_hint — подсказка, а не приговор (точность ~2/3).
			  4. Колонку source_tags НЕ УДАЛЯЙ, она нужна на этапе import.
			""");
	}

	// ------------------------------------------------------------------------

	private static string Normalize(string raw)
	{
		var builder = new StringBuilder(raw.Length);
		bool lastWasSpace = true;

		foreach (char c in raw.Trim().ToLowerInvariant())
		{
			char ch = c is '_' or '-' ? ' ' : c;

			if (ch == ' ')
			{
				if (lastWasSpace) continue;
				lastWasSpace = true;
			}
			else
			{
				lastWasSpace = false;
			}

			builder.Append(ch);
		}

		return builder.ToString().Trim();
	}

	/// <summary>Грубая английская сингуляризация. Намеренно консервативна.</summary>
	private static string Singularize(string word)
	{
		if (word.Length < 4 || !word.EndsWith('s') || word.EndsWith("ss") || word.EndsWith("us"))
			return word;

		if (word.EndsWith("ies") && word.Length > 4)
			return string.Concat(word.AsSpan(0, word.Length - 3), "y");

		if (word.EndsWith("ses") || word.EndsWith("shes") || word.EndsWith("ches") || word.EndsWith("xes"))
			return word[..^2];

		return word[..^1];
	}

	private static string Slugify(string value)
	{
		var builder = new StringBuilder(value.Length);

		foreach (char c in value.ToLowerInvariant())
		{
			if (char.IsAsciiLetterOrDigit(c))
				builder.Append(c);
			else if (builder.Length > 0 && builder[^1] != '_')
				builder.Append('_');
		}

		return builder.ToString().Trim('_');
	}

	private static string Csv(string value) =>
		value.Contains(',') || value.Contains('"') || value.Contains('\n')
			? '"' + value.Replace("\"", "\"\"") + '"'
			: value;

	private sealed class TagGroup(string normalized)
	{
		public string Normalized { get; } = normalized;
		public HashSet<string> RawForms { get; } = new(StringComparer.Ordinal);
		public HashSet<int> Photos { get; } = [];
		public List<string> MergedNotes { get; } = [];
	}

	// ------------------------------------------------------------------------
	// Подсказка по дублям: близкие центроиды И почти не встречаются вместе.
	//
	// Одна близость центроидов не работает: у любого подмножества фотографий
	// из одной поездки центроиды лежат в диапазоне 0.97–0.99, и настоящие дубли
	// не отличить от просто соседних понятий (мост и река).
	//
	// Добавление условия «почти не встречаются на одном фото» помогает: если
	// два тега про одно и то же, модель выбирала либо один, либо другой.
	// Точность примерно две трети — поэтому это колонка в CSV, а не автослияние.
	// ------------------------------------------------------------------------

	private static Dictionary<string, string> BuildDuplicateHints(List<TagGroup> groups, string sqlitePath)
	{
		const double MinCosine = 0.95;
		const double MaxJaccard = 0.02;

		var vectors = LoadVectors(sqlitePath);
		if (vectors.Count == 0)
		{
			Console.WriteLine("Векторов в sqlite нет — подсказки по дублям пропущены.");
			return [];
		}

		var centroids = new Dictionary<string, float[]>();

		foreach (var group in groups)
		{
			var accumulator = new double[Dim];
			int used = 0;

			foreach (int imageId in group.Photos)
			{
				if (!vectors.TryGetValue(imageId, out var vector))
					continue;

				for (int i = 0; i < Dim; i++)
					accumulator[i] += vector[i];

				used++;
			}

			if (used == 0)
				continue;

			double norm = Math.Sqrt(accumulator.Sum(x => x * x));
			if (norm <= 0)
				continue;

			var centroid = new float[Dim];
			for (int i = 0; i < Dim; i++)
				centroid[i] = (float)(accumulator[i] / norm);

			centroids[group.Normalized] = centroid;
		}

		var usable = groups.Where(g => centroids.ContainsKey(g.Normalized)).ToList();
		var hints = new Dictionary<string, List<string>>();

		for (int a = 0; a < usable.Count; a++)
		{
			for (int b = a + 1; b < usable.Count; b++)
			{
				var (first, second) = (usable[a], usable[b]);
				var va = centroids[first.Normalized];
				var vb = centroids[second.Normalized];

				double cosine = 0;
				for (int i = 0; i < Dim; i++)
					cosine += va[i] * vb[i];

				if (cosine < MinCosine)
					continue;

				int intersection = first.Photos.Count(second.Photos.Contains);
				int union = first.Photos.Count + second.Photos.Count - intersection;

				if (union == 0 || (double)intersection / union > MaxJaccard)
					continue;

				Add(hints, first.Normalized, $"{second.Normalized} ({cosine:F2})");
				Add(hints, second.Normalized, $"{first.Normalized} ({cosine:F2})");
			}
		}

		return hints.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value.Take(3)));

		static void Add(Dictionary<string, List<string>> map, string key, string value)
		{
			if (!map.TryGetValue(key, out var list))
				map[key] = list = [];

			list.Add(value);
		}
	}

	private static Dictionary<int, float[]> LoadVectors(string sqlitePath)
	{
		var result = new Dictionary<int, float[]>();

		using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
		sqlite.Open();

		var command = sqlite.CreateCommand();
		command.CommandText = "SELECT image_id, vector FROM embeddings";

		try
		{
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				byte[] blob = (byte[])reader["vector"];
				if (blob.Length != Dim * sizeof(float))
					continue;

				var vector = new float[Dim];
				Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
				result[reader.GetInt32(0)] = vector;
			}
		}
		catch (SqliteException)
		{
			// Таблицы embeddings нет — просто без подсказок
			return [];
		}

		return result;
	}
}
