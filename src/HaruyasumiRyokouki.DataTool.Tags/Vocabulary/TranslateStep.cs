using HaruyasumiRyokouki.DataTool.Common;
using Microsoft.Data.Sqlite;

namespace HaruyasumiRyokouki.DataTool.Tags.Vocabulary;

/// <summary>
/// Step translate: after the vocabulary is approved, the new tags that are left get labels and
/// aliases in en, ru and ja from the backend's AI, and the checks the site needs run on them.
/// Without problems the translations are approved at once and the next steps go on; with problems
/// translations.yaml waits for a person, like the vocabulary did. A file that waits is never
/// overwritten: a new translation starts only with a new approved vocabulary.
/// </summary>
internal sealed class TranslateStep(Translator translator, string vocabularyPath, string translationsPath, TextWriter output)
{
	public async Task<string?> RunAsync(SqliteConnection connection, StepReport report, CancellationToken cancellationToken)
	{
		VocabDb.Ensure(connection);
		string vocabulary = VocabDb.RequireApproved(connection, vocabularyPath);

		var state = VocabDb.ReadTranslationState(connection);
		if (state?.VocabularySha256 == vocabulary && File.Exists(translationsPath))
		{
			if (state.FileSha256 == VocabDb.FileSha256(translationsPath))
			{
				report.Message = "the translations are approved";
				return null;
			}

			report.Message = $"{translationsPath} waits for review: fix it, check it with 'haru-data review --check', approve it with 'haru-data review --approve'";
			output.WriteLine(report.Message);
			return "needs_review";
		}

		var tags = VocabDb.ReadApproved(connection)
			.Where(t => t.ExistingId is null)
			.Select(t => new TranslatedTag(t.Slug, t.Labels["en"], t.Aliases.Where(a => a.LanguageCode == "en").Select(a => a.Text)))
			.ToList();
		report.Count("tags to translate", tags.Count);

		var errors = new List<string>();
		await translator.TranslateAsync(tags, errors, cancellationToken);
		report.Count("not translated", errors.Count);

		// The file is checked as a person would check it, so the problems at its top and the review
		// agree. The problems take one line each, so checking the file with them in it gives the
		// line numbers of the final file.
		var file = TranslationsFile.From(tags);
		var slugs = tags.Select(t => t.Slug).ToList();
		List<string> Problems(VocabularyCheck.Result check) => errors
			.Concat(check.Issues.Select(i => $"{(i.IsError ? "error" : "warning")}: {(i.Slug is null ? "" : i.Slug + ": ")}{i.Message}"))
			.ToList();
		var check = VocabularyCheck.RunTranslations(file.Render([]), slugs);
		check = VocabularyCheck.RunTranslations(file.Render(Problems(check)), slugs);
		var problems = Problems(check);
		string text = file.Render(problems);
		File.WriteAllText(translationsPath, text, new System.Text.UTF8Encoding(false));

		bool clean = problems.Count == 0;
		VocabDb.SaveTranslations(connection, check.Tags, vocabulary, clean ? VocabDb.FileSha256(translationsPath) : null);
		report.Count("problems", problems.Count);

		if (clean)
		{
			report.Message = $"translated {tags.Count} tags without problems; the translations are approved ({translationsPath})";
			return null;
		}

		foreach (string problem in problems)
			output.WriteLine("problem: " + problem);
		report.Message = $"{problems.Count} problems in the translations: fix {translationsPath}, then 'haru-data review --approve'";
		output.WriteLine(report.Message);
		return "needs_review";
	}
}
