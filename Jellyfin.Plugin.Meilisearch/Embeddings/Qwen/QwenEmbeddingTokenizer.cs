using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.Plugin.Meilisearch.Embeddings.Qwen;

/// <summary>
/// Byte-level BPE tokenizer for Qwen3-Embedding, built from the model's <c>vocab.json</c> and
/// <c>merges.txt</c>.
/// </summary>
public sealed class QwenEmbeddingTokenizer
{
    private readonly BpeTokenizer _tokenizer;

    private QwenEmbeddingTokenizer(BpeTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// Loads the tokenizer from the model directory.
    /// </summary>
    /// <param name="descriptor">The model whose tokenizer files should be loaded.</param>
    /// <returns>The loaded tokenizer.</returns>
    public static QwenEmbeddingTokenizer Load(EmbeddingModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var options = new BpeOptions(ReadVocabulary(descriptor.GetFilePath(QwenEmbeddingModel.VocabFile)))
        {
            Merges = ReadMerges(descriptor.GetFilePath(QwenEmbeddingModel.MergesFile)),
            SpecialTokens = ReadSpecialTokens(descriptor.GetFilePath(QwenEmbeddingModel.AddedTokensFile)),
            ByteLevel = true,
            PreTokenizer = new QwenPreTokenizer(),

            // Byte-level BPE can represent any input, so there is no unknown token to fall back to.
            UnknownToken = null
        };

        return new QwenEmbeddingTokenizer(BpeTokenizer.Create(options));
    }

    /// <summary>
    /// Encodes text into token ids, truncating to <paramref name="maxTokens"/>.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="maxTokens">The maximum number of tokens to keep.</param>
    /// <returns>The token ids.</returns>
    public long[] Encode(string text, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || maxTokens <= 0)
        {
            return [];
        }

        var ids = _tokenizer.EncodeToIds(text, maxTokens, out _, out _);

        var result = new long[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            result[i] = ids[i];
        }

        return result;
    }

    private static List<KeyValuePair<string, int>> ReadVocabulary(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var vocabulary = new List<KeyValuePair<string, int>>(document.RootElement.GetPropertyCount());
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            vocabulary.Add(new KeyValuePair<string, int>(entry.Name, entry.Value.GetInt32()));
        }

        return vocabulary;
    }

    private static List<string> ReadMerges(string path)
        => File.ReadLines(path)
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    private static Dictionary<string, int> ReadSpecialTokens(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            specialTokens[entry.Name] = entry.Value.GetInt32();
        }

        return specialTokens;
    }
}
