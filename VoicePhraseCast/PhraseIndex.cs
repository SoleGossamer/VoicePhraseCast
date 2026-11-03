using FuzzySharp;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoicePhraseCast
{
    /// <summary>Результат поиска: ключ фразы, её файлы, оценка 0..100 и шаг конвейера, на котором найдено.</summary>
    public sealed record PhraseMatch(string Key, string[] Files, int Score, string Step);

    /// <summary>
    /// Неизменяемый индекс звуковых фраз. Строится целиком в фоне и подменяется одной записью ссылки,
    /// поэтому читатели (поток распознавания) никогда не видят полупустую базу.
    /// Все сравнения идут по нормализованным ключам: нижний регистр, «ё» = «е», без знаков препинания.
    /// </summary>
    public sealed partial class PhraseIndex
    {
        private sealed record Entry(string Key, string Flat, string[] Words, string[] Files);

        private static readonly HashSet<string> AudioExt =
            new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".flac", ".mpeg" };

        public static readonly PhraseIndex Empty = new(Array.Empty<Entry>());

        private readonly Entry[] _entries;
        private readonly Dictionary<string, Entry> _exact;

        private PhraseIndex(Entry[] entries)
        {
            _entries = entries;
            _exact = new Dictionary<string, Entry>(entries.Length, StringComparer.Ordinal);
            foreach (var e in entries) _exact[e.Key] = e;
        }

        public int Count => _entries.Length;

        /// <summary>Ключи индекса (нужны, например, для грамматики Vosk).</summary>
        public IEnumerable<string> Keys => _entries.Select(e => e.Key);

        public static bool IsAudio(string path) => AudioExt.Contains(Path.GetExtension(path));

        [GeneratedRegex(@"\s*\(.*?\)")]
        private static partial Regex Parens();

        [GeneratedRegex(@"\s+\d+$")]
        private static partial Regex TrailingNumber();

        [GeneratedRegex(@"[^\p{L}\p{M}\p{Nd}]+")]
        private static partial Regex NotLetters();

        /// <summary>Нормализация и для имён файлов, и для распознанного текста: иначе «Ну, давай!» не совпадёт с «ну давай».</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string lowered = s.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('ё', 'е');
            return NotLetters().Replace(lowered, " ").Trim();
        }

        /// <summary>Рекурсивно сканирует папку и группирует файлы по нормализованному ключу. Бросает OperationCanceledException при отмене.</summary>
        public static PhraseIndex Build(string folder, CancellationToken ct = default)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,          // недоступная подпапка пропускается, а не обрывает обход
                ReturnSpecialDirectories = false
            };

            foreach (string file in Directory.EnumerateFiles(folder, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsAudio(file)) continue;

                string name = Parens().Replace(Path.GetFileNameWithoutExtension(file), "");
                string key = TrailingNumber().Replace(Normalize(name), "").Trim();
                if (key.Length == 0) continue;      // «(1).mp3» раньше давал пустой ключ

                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<string>();
                list.Add(file);
            }

            var entries = groups
                .OrderBy(g => g.Key, StringComparer.Ordinal)   // фиксированный порядок: результат не зависит от обхода файловой системы
                .Select(g => new Entry(g.Key, g.Key.Replace(" ", ""), g.Key.Split(' '), g.Value.OrderBy(f => f, StringComparer.Ordinal).ToArray()))
                .ToArray();

            return new PhraseIndex(entries);
        }

        /// <summary>Индекс без файловой системы (для тестов): файлы подставляются фиктивные.</summary>
        internal static PhraseIndex FromKeys(params string[] keys)
        {
            var entries = keys
                .Select(Normalize)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => new Entry(k, k.Replace(" ", ""), k.Split(' '), new[] { k + ".mp3" }))
                .ToArray();
            return new PhraseIndex(entries);
        }

        // Оценки FuzzySharp собраны в трёх методах: если в вашей версии библиотеки сигнатуры другие, правится только здесь.
        private static int Ratio(string a, string b) => Fuzz.Ratio(a, b);
        private static int Partial(string a, string b) => Fuzz.PartialRatio(a, b);
        private static int TokenSet(string a, string b) => Fuzz.TokenSetRatio(a, b);

        private static PhraseMatch Hit(Entry e, int score, string step) => new(e.Key, e.Files, score, step);

        /// <summary>
        /// Ищет фразу. Шаги: 0 точное совпадение, 1 строгое сходство, 2 фрагмент длинной фразы,
        /// 3 набор слов, 4 частичное сходство. Результат детерминирован: при равенстве побеждает ключ, меньший по порядку.
        /// </summary>
        public PhraseMatch? Find(string? recognized)
        {
            string text = Normalize(recognized);
            if (text.Length == 0 || _entries.Length == 0) return null;

            string flat = text.Replace(" ", "");
            string[] words = text.Split(' ');
            int L = text.Length;

            // 0. Точное совпадение: O(1), без fuzzy.
            if (_exact.TryGetValue(text, out var exact)) return Hit(exact, 100, "exact");

            Entry? best = null;
            int bestScore = 0;

            // 1. Strict (> 85) с отсечением по длине. Для Ratio верхняя граница равна 2·min/(a+b),
            //    поэтому ключи вне окна [0.74·L; 1.35·L] не могут дать больше 85: отсечение не меняет результата.
            foreach (var e in _entries)
            {
                int k = e.Key.Length;
                if (k < L * 0.74 || k > L * 1.35) continue;
                int s = Ratio(text, e.Key);
                if (s > 85 && s > bestScore) { best = e; bestScore = s; }
            }
            if (best != null) return Hit(best, bestScore, "strict");

            // 2. Фрагмент длинной фразы. Дешёвая проверка (подстрока) идёт первой; из подходящих берётся самый короткий ключ.
            if (L > 5)
            {
                foreach (var e in _entries)
                {
                    if (e.Key.Length <= L || e.Flat.Length < flat.Length) continue;
                    bool fits = e.Flat.Contains(flat, StringComparison.Ordinal) || Partial(flat, e.Flat) >= 90;
                    if (fits && (best == null || e.Key.Length < best.Key.Length)) best = e;
                }
                if (best != null) return Hit(best, 100, "fragment");
            }

            // 3. TokenSet (> 85). Защита от слишком коротких и длинных кандидатов проверяется у КАЖДОГО кандидата,
            //    поэтому невалидный лидер не вытесняет валидного второго.
            foreach (var e in _entries)
            {
                int k = e.Key.Length;
                if (L > k + 4 || (L <= 5 && k > L + 4)) continue;
                int s = TokenSet(text, e.Key);
                if (s > 85 && s > bestScore) { best = e; bestScore = s; }
            }
            if (best != null) return Hit(best, bestScore, "tokenset");

            // 4. Partial (> 75) с прежними защитами.
            foreach (var e in _entries)
            {
                int k = e.Key.Length;
                if (L > k + 4 || (L <= 5 && k > L + 5)) continue;
                int s = Partial(text, e.Key);
                if (s <= 75 || s <= bestScore) continue;

                bool ok = k <= 4
                    ? Array.IndexOf(words, e.Key) >= 0
                    : words.Any(w => Array.IndexOf(e.Words, w) >= 0)
                      || e.Flat.Contains(flat, StringComparison.Ordinal)
                      || flat.Contains(e.Flat, StringComparison.Ordinal);

                if (ok) { best = e; bestScore = s; }
            }

            return best == null ? null : Hit(best, bestScore, "partial");
        }
    }
}
