using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.Plugin.Meilisearch.Embeddings.Qwen;

/// <summary>
/// The pre-tokenizer used by the Qwen2/Qwen3 tokenizer family: a GPT-4 style split that keeps
/// contractions, letter runs, digit runs, punctuation runs and whitespace as separate pieces before
/// byte-level BPE merges them.
/// </summary>
internal sealed partial class QwenPreTokenizer : PreTokenizer
{
    [GeneratedRegex(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.None,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex SplitPattern();

    /// <inheritdoc />
    public override IEnumerable<(int Offset, int Length)> PreTokenize(string? text)
        => string.IsNullOrEmpty(text) ? [] : Split(text);

    /// <inheritdoc />
    public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text)
        => text.IsEmpty ? [] : Split(text.ToString());

    private static List<(int Offset, int Length)> Split(string text)
    {
        var pieces = new List<(int Offset, int Length)>();
        foreach (var match in SplitPattern().EnumerateMatches(text))
        {
            if (match.Length > 0)
            {
                pieces.Add((match.Index, match.Length));
            }
        }

        return pieces;
    }
}
