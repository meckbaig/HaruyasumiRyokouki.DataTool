using System.Text;

namespace TagsTool;

/// <summary>Чтение CSV по RFC 4180: кавычки и удвоенные кавычки внутри поля.</summary>
internal static class CsvFile
{
	public static List<CsvRow> Read(string path)
	{
		if (!File.Exists(path))
			throw new FileNotFoundException($"Не найден файл {Path.GetFullPath(path)}");

		var rows = new List<CsvRow>();
		var lines = File.ReadAllLines(path, Encoding.UTF8);

		for (int i = 1; i < lines.Length; i++)   // строка 0 — заголовок
		{
			if (string.IsNullOrWhiteSpace(lines[i]))
				continue;

			var fields = SplitLine(lines[i]);

			if (fields.Count < 8)
				throw new InvalidOperationException(
					$"Строка {i + 1}: ожидалось 8 колонок, найдено {fields.Count}. " +
					"Колонки удалять нельзя — source_tags нужен на этапе import.");

			rows.Add(new CsvRow
			{
				Slug = fields[0].Trim(),
				Label = fields[1].Trim(),
				Count = int.TryParse(fields[2], out int count) ? count : 0,
				// fields[3] — band, нужен только тебе при разметке
				Action = fields[4].Trim(),
				SourceTags = fields[7].Split('|', StringSplitOptions.RemoveEmptyEntries).ToList()
			});
		}

		return rows;
	}

	private static List<string> SplitLine(string line)
	{
		var fields = new List<string>();
		var current = new StringBuilder();
		bool inQuotes = false;

		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];

			if (inQuotes)
			{
				if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
				{
					current.Append('"');
					i++;
				}
				else if (c == '"')
				{
					inQuotes = false;
				}
				else
				{
					current.Append(c);
				}
			}
			else if (c == '"')
			{
				inQuotes = true;
			}
			else if (c == ',')
			{
				fields.Add(current.ToString());
				current.Clear();
			}
			else
			{
				current.Append(c);
			}
		}

		fields.Add(current.ToString());
		return fields;
	}
}

internal sealed class CsvRow
{
	public required string Slug { get; init; }
	public required string Label { get; init; }
	public required int Count { get; init; }
	public required string Action { get; init; }
	public required List<string> SourceTags { get; init; }
}
