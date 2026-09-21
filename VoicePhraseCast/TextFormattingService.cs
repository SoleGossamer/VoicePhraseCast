using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoicePhraseCast
{
    public class TextFormattingService
    {
        private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "где", "как", "куда", "когда", "зачем", "почему", "кто", "что",
            "чей", "чья", "чье", "чьи", "сколько", "какой", "какая", "какое", "какие"
        };

        private static readonly string[] IntroductoryWords = new[]
        {
            "конечно", "кстати", "может быть", "во-первых", "во-вторых",
            "в-третьих", "пожалуйста", "кажется", "похоже", "к сожалению"
        };

        private static readonly string[] AsExceptions = new[]
        {
            "белый как", "гладко как", "быстрый как", "точно как", "так же как"
        };

        public string Format(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;

            string text = rawText.Trim();

            // 1. Первая буква заглавная
            text = char.ToUpper(text[0]) + text.Substring(1);

            // 2. Выделение вводных слов
            foreach (var intro in IntroductoryWords)
            {
                text = Regex.Replace(text, $@"^({intro})\b\s*", "$1, ", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, $@"\s+\b({intro})\b\s+", ", $1, ", RegexOptions.IgnoreCase);
            }

            // 3. Запятые перед союзами
            text = Regex.Replace(
                text,
                @"\b(?<!так\s+)(что|чтобы|когда|если|хотя|потому что|так как|но|а|куда|зачем)\b",
                ", $1",
                RegexOptions.IgnoreCase
            );

            // 4. Обработка союза «как» с исключением
            bool hasAsException = AsExceptions.Any(e => text.Contains(e, StringComparison.OrdinalIgnoreCase));
            if (!hasAsException)
            {
                text = Regex.Replace(text, @"\b(как)\b", ", как", RegexOptions.IgnoreCase);
            }

            // 5. Очистка дублирующихся запятых и лишних пробелов
            text = Regex.Replace(text, @"\s*,\s*", ", ");
            text = Regex.Replace(text, @",(\s*,)+", ",");

            if (text.StartsWith(","))
            {
                text = text.TrimStart(',', ' ').Trim();
                if (text.Length > 0)
                    text = char.ToUpper(text[0]) + text.Substring(1);
            }

            // 6. Определение знака в конце предложения
            text = ApplyFinalPunctuation(text);

            return text;
        }

        private string ApplyFinalPunctuation(string text)
        {
            // Если предложение уже заканчивается на восклицательный или вопросительный знак, оставляем как есть
            if (text.EndsWith("!") || text.EndsWith("?"))
                return text;

            // Если в конце случайно оказалась точка, убираем её
            if (text.EndsWith("."))
                text = text.TrimEnd('.');

            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return text;

            string firstWord = words[0].ToLower().Trim(',', '.');

            bool startsWithQuestion = QuestionWords.Contains(firstWord);

            bool isIndirectQuestion = text.StartsWith("я ", StringComparison.OrdinalIgnoreCase) ||
                                     text.StartsWith("скажи ", StringComparison.OrdinalIgnoreCase) ||
                                     text.StartsWith("посмотри ", StringComparison.OrdinalIgnoreCase);

            // Ставим вопросительный знак, только если это прямой вопрос
            if (startsWithQuestion && !isIndirectQuestion)
            {
                return text + "?";
            }

            // В остальных случаях просто возвращаем текст без точки на конце
            return text;
        }
    }
}