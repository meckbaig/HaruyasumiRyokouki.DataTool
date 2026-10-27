using TagsTool;

// ============================================================================
// Перенос тегов из photos.sqlite. Три этапа, между ними файлы на проверку.
//
//   tagstool extract   --sqlite photos.sqlite --out tags.csv
//   tagstool translate --in tags.csv --out tags.json --api ... --user ... --password ...
//   tagstool import    --in tags.json --api ... --user ... --password ... [--assign]
//
// Инструмент НЕ подключается к Postgres и не знает ключей от DeepSeek.
// Всё, что требует базы или LLM, делается запросами к твоему серверу:
//
//   POST /v1/Tags/completion   перевод тега      (GetTagCompletionCommand)
//   POST /v1/Tags              создание тега     (AddTagCommand)
//   GET  /v1/Tags              текущий словарь   (GetTagsQuery)
//   PATCH /v1/Media            простановка тегов (EditMediaCommand)
//
// Поэтому сервер должен быть запущен, а проект сервера трогать не нужно.
// ============================================================================

// Совместимость: пока инструмент жил внутри проекта сервера, команда писалась
// как "tags import ...". Здесь это отдельная программа, и префикс не нужен —
// но молча ронять человека в справку из-за лишнего слова незачем.
if (args.Length > 0 && args[0].Equals("tags", StringComparison.OrdinalIgnoreCase))
	args = args[1..];

if (args.Length == 0)
{
	PrintUsage();
	return 1;
}

var options = Options.Parse(args[1..]);

try
{
	switch (args[0])
	{
		case "extract":
			await ExtractStage.RunAsync(
				sqlitePath: options.Require("sqlite"),
				outputPath: options.Get("out") ?? "tags.csv",
				minCount: int.Parse(options.Get("min-count") ?? "3"));
			return 0;

		case "translate":
			using (var api = BackendApi.Create(options))
			{
				await TranslateStage.RunAsync(
					api,
					csvPath: options.Require("in"),
					outputPath: options.Get("out") ?? "tags.json",
					delayMs: int.Parse(options.Get("delay") ?? "300"),
					threads: ParseThreads(options.Get("threads")));
			}
			return 0;

		case "import":
			using (var api = BackendApi.Create(options))
			{
				await ImportStage.RunAsync(
					api,
					jsonPath: options.Require("in"),
					sqlitePath: options.Get("sqlite"),
					assignToMedia: options.Has("assign"));
			}
			return 0;

		case "embeddings":
			// Единственная команда, которая ходит в Postgres напрямую:
			// на сервере нет и не будет логики импорта векторов.
			// Сервер при этом не нужен и не запускается.
			await EmbeddingsStage.RunAsync(
				sqlitePath: options.Require("sqlite"),
				connectionString: options.GetOrEnv("connection", "TAGSTOOL_CONNECTION"),
				table: options.Get("table") ?? "media_embeddings",
				dryRun: options.Has("dry-run"));
			return 0;

		default:
			// Раньше здесь молча печаталась справка, и было непонятно, что
			// именно не так. Теперь причина называется явно.
			Console.Error.WriteLine($"Неизвестная команда: '{args[0]}'");
			Console.Error.WriteLine("Ожидалась одна из: extract, translate, import, embeddings.");
			Console.Error.WriteLine();
			PrintUsage();
			return 1;
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine();
	Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
	return 1;
}

// Валидация здесь, а не внутри этапа: ошибка в аргументах должна вылезти
// сразу, до проверки доступности сервера и чтения файлов.
static int ParseThreads(string? raw)
{
	if (string.IsNullOrWhiteSpace(raw))
		return 1;   // по умолчанию последовательно, как было

	if (!int.TryParse(raw, out int threads))
		throw new ArgumentException($"--threads: '{raw}' не число");

	if (threads < 1 || threads > TranslateStage.MaxThreads)
		throw new ArgumentException(
			$"--threads: допустимо от 1 до {TranslateStage.MaxThreads}, получено {threads}");

	return threads;
}

static void PrintUsage() => Console.WriteLine("""
Перенос тегов из photos.sqlite. Три этапа, между ними правишь файлы руками.

Запуск из папки TagsTool:   dotnet run -- <команда> <параметры>
Собранным бинарником:       tagstool <команда> <параметры>

Команда — ПЕРВЫЙ аргумент, слово "tags" перед ней не нужно:

Теги (через HTTP API сервера):
extract    --sqlite <path> [--out tags.csv] [--min-count 3]
translate  --in tags.csv   [--out tags.json] [--threads 1] [--delay 300] <доступ>
import     --in tags.json  [--sqlite <path>] [--assign]                  <доступ>

Векторы (напрямую в Postgres, сервер не нужен):
embeddings --sqlite <path> [--connection <строка>] [--table media_embeddings] [--dry-run]

Например:  dotnet run -- import --in tags.json --user admin --password ...
           dotnet run -- embeddings --sqlite ../photos.sqlite --dry-run

Доступ к серверу (нужен только на этапах translate и import):
--api       базовый адрес, по умолчанию http://localhost:5101
--user      логин Basic-авторизации   (или переменная TAGSTOOL_USER)
--password  пароль Basic-авторизации  (или переменная TAGSTOOL_PASSWORD)

Доступ к базе (нужен только для embeddings):
--connection строка подключения к Postgres (или переменная TAGSTOOL_CONNECTION)
--table      имя таблицы, по умолчанию media_embeddings
--dry-run    прочитать sqlite и проверить формат, к базе не подключаться

Прочее:
--min-count порог для ПРЕДЗАПОЛНЕНИЯ action=keep (по умолчанию 3).
			Ничего не удаляет: в CSV попадают все теги без исключения.
--threads   сколько тегов переводить одновременно, 1..2500 (по умолчанию 1).
			Каждый поток — запрос к серверу, который дёргает DeepSeek,
			так что упереться можно в рейт-лимит провайдера. Упавшие теги
			не теряются: повторный запуск доберёт только их.
--delay     пауза после каждого тега, мс. При --threads > 1 действует
			на каждый поток отдельно, то есть работает как троттлинг.
--assign    помимо словаря расставить теги фотографиям

Первый этап работает полностью офлайн: сервер не нужен.
Ctrl+C на этапе translate дописывает файл перед выходом.
""");
