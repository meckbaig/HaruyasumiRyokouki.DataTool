namespace HaruyasumiRyokouki.DataTool.Tags;

/// <summary>
/// Synonym candidates from image vectors: two tags whose photos look alike (close centroids)
/// and almost never appear on the same photo.
///
/// Closeness alone does not work: centroids of any two subsets of one trip's photos lie at
/// 0.97 to 0.99, so true synonyms cannot be told from neighbours (bridge and river). If two
/// tags mean the same, the model picked one or the other for a photo, which is what the
/// low co-occurrence catches. About two thirds of the candidates were right on the first
/// run, so they are a hint for the language model, never a merge by themselves.
/// </summary>
internal static class DuplicateHints
{
	/// <summary>Minimum cosine of the two centroids.</summary>
	public const double MinCosine = 0.95;

	/// <summary>Maximum share of photos the two tags have in common (Jaccard index).</summary>
	public const double MaxJaccard = 0.02;

	/// <summary>A centroid of one or two photos is just those photos; comparing it says nothing.</summary>
	public const int MinPhotos = 5;

	/// <summary>Returns up to three candidates for each tag that has any, closest first.</summary>
	/// <param name="vectors">L2-normalized vectors by file key; files without one are skipped.</param>
	public static Dictionary<string, List<(string Tag, double Cosine)>> Build(
		IReadOnlyList<TagGroup> groups, IReadOnlyDictionary<string, float[]> vectors)
	{
		var centroids = new Dictionary<string, float[]>(StringComparer.Ordinal);

		foreach (var group in groups.Where(g => g.Keys.Count >= MinPhotos))
		{
			var centroid = Centroid(group.Keys, vectors);
			if (centroid is not null)
				centroids[group.Normalized] = centroid;
		}

		var usable = groups.Where(g => centroids.ContainsKey(g.Normalized)).ToList();
		var hints = new Dictionary<string, List<(string Tag, double Cosine)>>(StringComparer.Ordinal);

		for (int a = 0; a < usable.Count; a++)
		{
			for (int b = a + 1; b < usable.Count; b++)
			{
				var (first, second) = (usable[a], usable[b]);
				double cosine = Dot(centroids[first.Normalized], centroids[second.Normalized]);
				if (cosine < MinCosine)
					continue;

				int intersection = first.Keys.Count(second.Keys.Contains);
				int union = first.Keys.Count + second.Keys.Count - intersection;
				if ((double)intersection / union > MaxJaccard)
					continue;

				Add(first.Normalized, second.Normalized, cosine);
				Add(second.Normalized, first.Normalized, cosine);
			}
		}

		return hints.ToDictionary(
			kv => kv.Key,
			kv => kv.Value.OrderByDescending(h => h.Cosine).Take(3).ToList(),
			StringComparer.Ordinal);

		void Add(string tag, string other, double cosine)
		{
			if (!hints.TryGetValue(tag, out var list))
				hints[tag] = list = [];
			list.Add((other, cosine));
		}
	}

	private static float[]? Centroid(IEnumerable<string> keys, IReadOnlyDictionary<string, float[]> vectors)
	{
		double[]? sum = null;

		foreach (string key in keys)
		{
			if (!vectors.TryGetValue(key, out var vector))
				continue;

			sum ??= new double[vector.Length];
			for (int i = 0; i < vector.Length; i++)
				sum[i] += vector[i];
		}

		if (sum is null)
			return null;

		double norm = Math.Sqrt(sum.Sum(x => x * x));
		return norm > 0 ? sum.Select(x => (float)(x / norm)).ToArray() : null;
	}

	private static double Dot(float[] a, float[] b)
	{
		double sum = 0;
		for (int i = 0; i < a.Length; i++)
			sum += a[i] * b[i];
		return sum;
	}
}
