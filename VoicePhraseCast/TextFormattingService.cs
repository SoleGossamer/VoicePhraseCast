using System.Text.RegularExpressions;

namespace VoicePhraseCast
{
    /// <summary>
    /// Лёгкая расстановка заглавной буквы, запятых и «?» для диктовки в игровой чат.
    /// Все регулярные выражения сгенерированы на этапе компиляции (кэш статических Regex на 15 шаблонов раньше не хватало,
    /// и 20 динамических паттернов вводных слов разбирались заново на каждую фразу).
    /// </summary>
    public sealed partial class TextFormattingService
    {
        private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "где", "как", "куда", "когда", "зачем", "почему", "кто", "что",
            "чей", "чья", "чье", "чьи", "сколько", "какой", "какая", "какое", "какие"
        };

        private static readonly string[] AsExceptions =
        {
            "белый как", "гладко как", "быстрый как", "точно как", "так же как"
        };

        // Вводные слова: одна альтернация вместо цикла по двадцати динамическим шаблонам.
        private const string Intro = @"конечно|кстати|может\sбыть|во-первых|во-вторых|в-третьих|пожалуйста|кажется|похоже|к\sсожалению";

        [GeneratedRegex(@"^(?:" + Intro + @")(?![\p{L}-])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex IntroStartRegex();

        [GeneratedRegex(@"\s+(?<![\p{L}-])(" + Intro + @")(?![\p{L}-])\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex IntroMidRegex();

        // Один проход вместо двух: «так как» больше не превращается в «так, как», «что-то» не трогается,
        // а исключение для «белый как» действует только на своё «как», а не на все «как» во фразе.
        [GeneratedRegex(@"(?<![\p{L}-])(?<!так\s)(?:потому\sчто|так\sкак|чтобы|что|когда|если|хотя|но|а|куда|зачем|как)(?![\p{L}-])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ConjunctionRegex();

        [GeneratedRegex(@"\s*,\s*")]
        private static partial Regex SpacedCommaRegex();

        [GeneratedRegex(@",(\s*,)+")]
        private static partial Regex RepeatedCommaRegex();

        public string Format(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;

            string text = rawText.Trim();

            // 1. Первая буква заглавная
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);

            // 2. Выделение вводных слов запятыми
            text = IntroStartRegex().Replace(text, m => m.Value.TrimEnd() + ", ");
            text = IntroMidRegex().Replace(text, m => ", " + m.Groups[1].Value + ", ");

            // 3. Запятые перед союзами (включая «как», кроме устойчивых выражений)
            string source = text;
            text = ConjunctionRegex().Replace(source, m =>
                m.Value.Equals("как", StringComparison.OrdinalIgnoreCase) && IsIdiom(source, m.Index + m.Length)
                    ? m.Value
                    : ", " + m.Value);

            // 4. Очистка дублирующихся запятых и лишних пробелов
            text = SpacedCommaRegex().Replace(text, ", ");
            text = RepeatedCommaRegex().Replace(text, ",");

            if (text.StartsWith(','))
            {
                text = text.TrimStart(',', ' ').Trim();
                if (text.Length > 0)
                    text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            }

            text = text.TrimEnd(',', ' ');

            // 5. Знак в конце предложения
            return ApplyFinalPunctuation(text);
        }

        private static bool IsIdiom(string s, int end)
        {
            ReadOnlySpan<char> head = s.AsSpan(0, end);
            foreach (string idiom in AsExceptions)
            {
                if (head.EndsWith(idiom, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ApplyFinalPunctuation(string text)
        {
            // Если предложение уже заканчивается на восклицательный или вопросительный знак, оставляем как есть
            if (text.EndsWith('!') || text.EndsWith('?'))
                return text;

            // Если в конце случайно оказалась точка, убираем её
            if (text.EndsWith('.'))
                text = text.TrimEnd('.');

            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return text;

            string firstWord = words[0].ToLowerInvariant().Trim(',', '.');

            bool startsWithQuestion = QuestionWords.Contains(firstWord);

            bool isIndirectQuestion = text.StartsWith("я ", StringComparison.OrdinalIgnoreCase)
                                   || text.StartsWith("скажи ", StringComparison.OrdinalIgnoreCase)
                                   || text.StartsWith("посмотри ", StringComparison.OrdinalIgnoreCase);

            // Вопросительный знак ставится только для прямого вопроса
            if (startsWithQuestion && !isIndirectQuestion)
                return text + "?";

            return text;
        }
    }
}
