using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace VoicePhraseCast
{
    /// <summary>Замеры задержки «отпустил клавишу → звук в кабеле». Работают только в Debug-сборке (вызовы Mark вырезаются в Release).</summary>
    internal static class LatencyTrace
    {
        private static long _t0;

        public static void Start() => Interlocked.Exchange(ref _t0, Stopwatch.GetTimestamp());

        [Conditional("DEBUG")]
        public static void Mark(string what) =>
            Debug.WriteLine($"[Latency] {what}: {Stopwatch.GetElapsedTime(Interlocked.Read(ref _t0)).TotalMilliseconds:F0} мс");
    }

    /// <summary>
    /// Фраза, декодированная один раз в моно float на частоте микшера. Массив только читается, поэтому им безопасно делиться.
    ///
    /// Выравнивание громкости. Пик файла не говорит, как громко файл звучит: у шёпота с одним щелчком пик высокий, а сама речь тихая,
    /// у плотного рок-вокала пик и громкость почти совпадают. Поэтому при декодировании (один раз на файл) измеряется громкость
    /// АКТИВНОЙ части (LoudnessMeter: без тишины по краям и пауз, с взвешиванием по слуху), а одиночные выбросы выше
    /// «громкость + ReferenceCrestDb» срезаются ограничителем с упреждением. После этого GainFor приводит все фразы к одной громкости
    /// так, чтобы пик обычной речи лёг на потолок, заданный ползунком, и ни один отсчёт не оказался выше потолка.
    /// Уровень на горячем пути — по-прежнему один множитель на отсчёт, он считается из готовых чисел, поэтому ползунок потолка
    /// работает на лету и кэш от него не зависит. От настройки ReferenceCrestDb кэш зависит (она задаёт порог ограничителя), поэтому
    /// при её смене фразы готовятся заново (см. PhraseAudioCache.ReferenceCrestDb).
    ///
    /// Гейт пауз. Тихую фразу приходится усиливать на 12 дБ и больше, и вместе с голосом поднимается шум записи: в паузах слышен шип.
    /// Если фразу вообще можно усилить настолько, при декодировании строится огибающая гейта (PauseGate): она приглушает только паузы,
    /// не глубже 12 дБ, с упреждением и плавными Attack/Release. Применяет её CachedSoundProvider, сила зависит от усиления
    /// (GateStrengthFor): до +12 дБ гейт выключен, с +15 дБ действует в полную силу. Громкость и пик гейт не меняют.
    /// </summary>
    public sealed class CachedSound
    {
        private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

        /// <summary>Пик тише этого (−80 dBFS) — цифровая тишина или дитер: поднимать в таком файле нечего.</summary>
        private const float SilencePeak = 1e-4f;

        /// <summary>Предел подъёма тихой фразы, дБ: запись с громкостью −50 дБ не превращается в шипение на полной громкости.</summary>
        public const float MaxBoostDb = 30f;

        /// <summary>
        /// Запас «пик над громкостью» по умолчанию, дБ (настройка Settings.ReferenceCrestDb). Ползунок задаёт потолок пика, а громкость,
        /// до которой подтягиваются все фразы, ниже потолка на этот запас. У обычной речи запас 12–20 дБ (пик у потолка), у плотной музыки
        /// 8–10 дБ (пик ниже потолка); всё, что выше запаса (щелчок в шёпоте, взрывной звук), срезается при декодировании.
        /// Меньше запас (10–12) — плотнее и громче при том же потолке, больше (18) — мягче и тише.
        /// </summary>
        public const float DefaultReferenceCrestDb = 15f;
        public const float MinReferenceCrestDb = 8f;
        public const float MaxReferenceCrestDb = 24f;

        /// <summary>Значение настройки → допустимое: NaN и бесконечность дают значение по умолчанию, остальное зажимается в 8…24 дБ.</summary>
        public static float ClampReferenceCrest(double value) =>
            double.IsFinite(value) ? (float)Math.Clamp(value, MinReferenceCrestDb, MaxReferenceCrestDb) : DefaultReferenceCrestDb;

        /// <summary>Запас «пик над громкостью», с которым подготовлена эта фраза: порог ограничителя и цель GainFor.</summary>
        public float ReferenceCrestDb { get; }

        public float[] Samples { get; }
        public TimeSpan Duration { get; }

        /// <summary>Наибольший по модулю отсчёт ПОСЛЕ среза выбросов: 0…1 (у файлов с перегрузом и после ресемплинга бывает чуть больше 1).</summary>
        public float Peak { get; }

        /// <summary>Пик после среза выбросов в dBFS (−∞ для полной тишины).</summary>
        public float PeakDb => DbOf(Peak);

        /// <summary>Пик файла до среза выбросов, dBFS (для журнала).</summary>
        public float RawPeakDb { get; }

        /// <summary>
        /// Громкость активной части, дБ: K-взвешенная, без тишины по краям и пауз внутри (0 дБ — полная шкала; синус 0 dBFS даёт −2,3).
        /// NaN — измерить нечего (тишина), тогда уровень задаётся только по пику.
        /// </summary>
        public float LoudnessDb { get; }

        /// <summary>Какая доля блоков вошла в расчёт громкости (0…1); остальное — паузы и тишина.</summary>
        public float ActiveFraction { get; }

        /// <summary>На сколько дБ ограничитель опустил самый высокий выброс; 0 — вмешательства не было.</summary>
        public float LimitedDb { get; }

        /// <summary>
        /// Огибающая гейта пауз: линейные множители (≈ 0,25…1) в узлах через GateHop отсчётов, в конце два лишних узла для интерполяции.
        /// null — гейт этой фразе не нужен (её нельзя усилить больше чем на PauseGate.StartGainDb, пауз нет или анализ не удался).
        /// </summary>
        public float[]? GateEnvelope { get; }

        /// <summary>Расстояние между узлами огибающей, отсчётов (0, если огибающей нет).</summary>
        public int GateHop { get; }

        /// <summary>Наибольшее ослабление в паузах при полной силе гейта, дБ (для журнала).</summary>
        public float GateDepthDb { get; }

        /// <summary>Какую долю времени гейт закрыт не менее чем на 3 дБ при полной силе (для журнала).</summary>
        public float GateClosedFraction { get; }

        public long Bytes => Samples.Length * 4L + (GateEnvelope?.Length ?? 0) * 4L;

        /// <summary>Environment.TickCount64 последнего обращения (0 — ещё не учтена кэшем). Поле, а не свойство: нужен Interlocked.</summary>
        public long LastUsed;

        /// <summary>
        /// Декодирует файл и готовит фразу: громкость, срез выбросов, огибающая гейта пауз. referenceCrestDb — запас «пик над громкостью»
        /// (Settings.ReferenceCrestDb), приводится к 8…24 дБ.
        /// </summary>
        public CachedSound(string path, int sampleRate, float referenceCrestDb = DefaultReferenceCrestDb)
        {
            ReferenceCrestDb = ClampReferenceCrest(referenceCrestDb);

            using var reader = new AudioFileReader(path);
            if (reader.TotalTime > MaxDuration)
                throw new InvalidDataException($"Файл длиннее {MaxDuration.TotalMinutes:0} мин, это не фраза: {Path.GetFileName(path)}");

            // Сначала в моно (ресемплировать один канал вдвое дешевле), затем, только при необходимости, в частоту микшера.
            // Среднее двух каналов (0.5 + 0.5): двойное моно не становится громче и не клиппирует.
            ISampleProvider source = reader;
            int channels = source.WaveFormat.Channels;
            if (channels == 2)
                source = source.ToMono(0.5f, 0.5f);
            else if (channels != 1)
                throw new InvalidDataException($"Поддерживаются только моно и стерео файлы ({channels} кан.): {Path.GetFileName(path)}");

            if (source.WaveFormat.SampleRate != sampleRate)
                source = new WdlResamplingSampleProvider(source, sampleRate);

            var data = new float[(int)(reader.TotalTime.TotalSeconds * sampleRate) + sampleRate];
            int total = 0, read;
            while ((read = source.Read(data, total, data.Length - total)) > 0)
            {
                total += read;
                if (total == data.Length) Array.Resize(ref data, data.Length * 2);
            }
            Array.Resize(ref data, total);

            // Громкость и срез выбросов считаются по итоговым отсчётам (после микса в моно и ресемплинга): именно они уйдут в микшер.
            // Сбой анализа не должен лишить фразу голоса: тогда остаётся прежняя нормализация по пику.
            SoundAnalysis analysis;
            try
            {
                analysis = LoudnessMeter.Analyze(data, sampleRate, ReferenceCrestDb, SilencePeak);   // запас этой фразы: порог ограничителя
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cache] Громкость не измерена, уровень только по пику: {ex.Message}");
                float peak = LoudnessMeter.SanitizeAndPeak(data);
                analysis = new SoundAnalysis(float.NaN, 0f, peak, peak, 0f);
            }

            Samples = data;
            Peak = analysis.Peak;
            RawPeakDb = DbOf(analysis.RawPeak);
            LoudnessDb = analysis.LoudnessDb;
            ActiveFraction = analysis.ActiveFraction;
            LimitedDb = analysis.LimitedDb;
            Duration = TimeSpan.FromSeconds((double)total / sampleRate);

            // Огибающая гейта нужна, только если фразу вообще можно усилить настолько, чтобы шум стал слышен: наибольшее усиление — при
            // потолке 0 dBFS (край ползунка). Сбой анализа не лишает фразу голоса: она просто идёт без гейта.
            if (GainDbFor(0f) > PauseGate.StartGainDb)
            {
                try
                {
                    GateEnvelope = PauseGate.Build(data, sampleRate, LoudnessDb, out int hop, out float depth, out float closed);
                    if (GateEnvelope != null)
                    {
                        GateHop = hop;
                        GateDepthDb = depth;
                        GateClosedFraction = closed;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cache] Гейт пауз не построен, фраза пойдёт без него: {ex.Message}");
                }
            }
        }

        private static float DbOf(float value) => value > 0f ? 20f * MathF.Log10(value) : float.NegativeInfinity;

        /// <summary>
        /// Усиление для кабеля (и для наушников: там сверху ещё ползунок «для себя») при потолке пика peakCeilingDb (dBFS), дБ.
        /// Два условия, действует более строгое:
        ///  • громкость: активная часть фразы приводится к уровню (потолок − ReferenceCrestDb), то есть все фразы звучат одинаково
        ///    громко, как бы ни различались пики и паузы;
        ///  • потолок: самый высокий отсчёт после усиления не выше peakCeilingDb, перегруза не бывает при любом потолке ≤ 0.
        /// Если громкость измерить не удалось (NaN), остаётся только второе условие: прежняя пиковая нормализация.
        /// Тишину не усиливаем (иначе шум вырос бы в тысячи раз), подъём ограничен MaxBoostDb.
        /// </summary>
        public float GainDbFor(float peakCeilingDb)
        {
            if (Peak < SilencePeak) return 0f;

            float gainDb = peakCeilingDb - PeakDb;
            if (!float.IsNaN(LoudnessDb))
                gainDb = Math.Min(gainDb, peakCeilingDb - ReferenceCrestDb - LoudnessDb);

            return Math.Min(gainDb, MaxBoostDb);
        }

        /// <summary>Линейный множитель, соответствующий GainDbFor.</summary>
        public float GainFor(float peakCeilingDb) => MathF.Pow(10f, GainDbFor(peakCeilingDb) / 20f);

        /// <summary>
        /// Сила гейта пауз, 0…1, при потолке peakCeilingDb: 0 пока усиление не больше PauseGate.StartGainDb (+12 дБ), дальше растёт
        /// линейно по дБ и с PauseGate.FullGainDb (+15 дБ) равна 1. Зависит только от усиления выравнивания, не от ползунка «для себя»:
        /// в кабеле и в наушниках пауза звучит одинаково. 0 и тогда, когда огибающей нет.
        /// </summary>
        public float GateStrengthFor(float peakCeilingDb)
        {
            if (GateEnvelope == null) return 0f;

            float t = (GainDbFor(peakCeilingDb) - PauseGate.StartGainDb) / (PauseGate.FullGainDb - PauseGate.StartGainDb);
            return t > 0f ? Math.Min(t, 1f) : 0f;   // NaN тоже даёт 0
        }
    }

    /// <summary>Итог анализа файла при декодировании (см. LoudnessMeter.Analyze).</summary>
    internal readonly record struct SoundAnalysis(float LoudnessDb, float ActiveFraction, float RawPeak, float Peak, float LimitedDb);

    /// <summary>
    /// Измерение «слышимой» громкости файла и срез одиночных выбросов. Выполняется один раз при декодировании, в фоновом потоке
    /// (по оценке, а не по замеру: единицы миллисекунд на десять секунд звука); на горячем пути от этого остаётся один множитель.
    ///
    /// Громкость считается по мотивам ITU-R BS.1770 (LUFS), без претензии на соответствие стандарту:
    ///  • K-взвешивание (ступенька +4 дБ выше ≈ 1,7 кГц, срез ниже ≈ 38 Гц): шёпот и вокал с разным спектром сравниваются так,
    ///    как их слышит ухо, а не как их видит голый RMS;
    ///  • тишина по краям отбрасывается по отсчётам (ниже −45 dBFS), паузы внутри файла по блокам: блок 100 мс с шагом 50 мс,
    ///    абсолютный порог −70 дБ и относительный −10 дБ от средней энергии. Длинные паузы громкость не занижают, а один щелчок
    ///    среди десятков тысяч отсчётов её не определяет.
    ///
    /// Выбросы: всё, что выше «громкость + запас», опускается ограничителем с упреждением 4 мс. Усиление меняется плавно (скользящий
    /// минимум требуемого усиления и скользящее среднее по тому же окну), порог не превышается ни на одном отсчёте, а за пределами
    /// ±8 мс от выброса звук остаётся бит в бит прежним. Память постоянная, время линейное.
    /// </summary>
    internal static class LoudnessMeter
    {
        private const float EdgeSilenceDb = -45f;        // тишина по краям файла (по отсчётам), dBFS
        private const double AbsoluteGateDb = -70.0;     // блоки тише — цифровая тишина или шумовой пол
        private const double RelativeGateDb = -10.0;     // блоки тише средней энергии на столько — паузы внутри файла
        private const int BlockMs = 100;                 // окно измерения; короче окна файл считается целиком
        private const int HopMs = 50;                    // шаг окна (перекрытие 50 %)
        private const int LimiterLookaheadMs = 4;        // упреждение ограничителя

        public static SoundAnalysis Analyze(float[] data, int sampleRate, float referenceCrestDb, float silencePeak)
        {
            float rawPeak = SanitizeAndPeak(data);
            if (rawPeak < silencePeak)
                return new SoundAnalysis(float.NaN, 0f, rawPeak, rawPeak, 0f);

            MeasureLoudness(data, sampleRate, out float loudnessDb, out float activeFraction);
            if (float.IsNaN(loudnessDb))
                return new SoundAnalysis(float.NaN, 0f, rawPeak, rawPeak, 0f);

            // Выше «громкость + запас» не должно быть ничего: иначе подъём до нужной громкости упирался бы в потолок из-за одного щелчка.
            float limit = MathF.Pow(10f, (loudnessDb + referenceCrestDb) / 20f);
            float peak = rawPeak;
            float limitedDb = 0f;
            if (rawPeak > limit)
            {
                float minGain = LimitPeaks(data, limit, LimiterLookaheadMs * sampleRate / 1000);
                limitedDb = -20f * MathF.Log10(minGain);
                peak = SanitizeAndPeak(data);
            }

            return new SoundAnalysis(loudnessDb, activeFraction, rawPeak, peak, limitedDb);
        }

        // Заодно заменяет NaN и бесконечности нулями: один такой отсчёт навсегда испортил бы состояние фильтров.
        internal static float SanitizeAndPeak(float[] data)
        {
            float peak = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float a = data[i];
                if (!float.IsFinite(a))
                {
                    data[i] = 0f;
                    continue;
                }

                if (a < 0f) a = -a;
                if (a > peak) peak = a;
            }
            return peak;
        }

        private static void MeasureLoudness(float[] data, int sampleRate, out float loudnessDb, out float activeFraction)
        {
            loudnessDb = float.NaN;
            activeFraction = 0f;

            // 1. Тишина по краям (по отсчётам). Если весь файл тише порога, считаем его целиком.
            int n = data.Length;
            float edge = MathF.Pow(10f, EdgeSilenceDb / 20f);
            int start = 0, end = n;
            while (start < n && Math.Abs(data[start]) < edge) start++;
            if (start == n)
            {
                start = 0;
            }
            else
            {
                while (Math.Abs(data[end - 1]) < edge) end--;
            }

            int length = end - start;
            if (length <= 0) return;

            // 2. Энергия K-взвешенного сигнала по сегментам длиной в шаг окна; блок — несколько соседних сегментов.
            int hop = HopMs * sampleRate / 1000;
            int blockSegs = BlockMs / HopMs;
            if (length < hop * blockSegs)
            {
                hop = length;   // короче одного блока: весь активный участок — один блок
                blockSegs = 1;
            }
            int segCount = length / hop;

            var segments = new double[segCount];
            var filter = new KWeighting(sampleRate);
            int pos = start;
            for (int s = 0; s < segCount; s++)
            {
                double energy = 0.0;
                for (int i = 0; i < hop; i++)
                {
                    double y = filter.Process(data[pos++]);
                    energy += y * y;
                }
                segments[s] = energy;
            }

            int blocks = segCount - blockSegs + 1;
            var meanSquare = new double[blocks];
            double window = 0.0;
            for (int s = 0; s < segCount; s++)
            {
                window += segments[s];
                if (s >= blockSegs) window -= segments[s - blockSegs];
                if (s >= blockSegs - 1) meanSquare[s - blockSegs + 1] = Math.Max(window, 0.0) / ((double)blockSegs * hop);
            }

            // 3. Абсолютный порог убирает тишину, относительный — паузы: в расчёт входят блоки не тише −10 дБ от средней энергии.
            double absGate = Math.Pow(10.0, AbsoluteGateDb / 10.0);
            double sum = 0.0;
            int count = 0;
            for (int b = 0; b < blocks; b++)
            {
                if (meanSquare[b] >= absGate)
                {
                    sum += meanSquare[b];
                    count++;
                }
            }
            if (count == 0) return;

            double relGate = Math.Max(absGate, sum / count * Math.Pow(10.0, RelativeGateDb / 10.0));
            double keptSum = 0.0;
            int kept = 0;
            for (int b = 0; b < blocks; b++)
            {
                if (meanSquare[b] >= relGate)
                {
                    keptSum += meanSquare[b];
                    kept++;
                }
            }
            if (kept == 0) return;

            loudnessDb = (float)(10.0 * Math.Log10(keptSum / kept));
            activeFraction = (float)kept / blocks;
        }

        /// <summary>
        /// Ограничитель выбросов с упреждением (на месте). Для отсчёта k требуемое усиление need[k] = min(1, порог / |x[k]|).
        /// m[j] — минимум need за окно [j − w, j] (монотонная очередь), усиление отсчёта t — среднее m[t..t + w]. Окно минимума для
        /// каждого j из [t, t + w] содержит t, поэтому среднее не больше need[t]: порог не превышается ни на одном отсчёте.
        /// Возвращает наименьшее применённое усиление (1 — вмешательства не было).
        /// </summary>
        private static float LimitPeaks(float[] x, float threshold, int w)
        {
            int n = x.Length;
            int ring = w + 1;
            var queueIndex = new int[ring];      // монотонная очередь: индексы и значения need, значения растут от головы к хвосту
            var queueValue = new float[ring];
            var minimums = new float[ring];      // последние w + 1 значений m
            int head = 0, count = 0, slot = 0;
            double sum = 0.0;                    // сумма minimums; разность считается в double, поэтому ошибка не копится
            float minGain = 1f;

            for (int k = 0; k < n + w; k++)
            {
                float need = 1f;
                if (k < n)
                {
                    float a = Math.Abs(x[k]);
                    if (a > threshold) need = threshold / a;
                }

                // Окно минимума [k − w, k]: сначала выбывает устаревший, потом убираются не лучшие «хвосты», потом входит новый.
                if (count > 0 && queueIndex[head] < k - w)
                {
                    if (++head == ring) head = 0;
                    count--;
                }
                while (count > 0)
                {
                    int back = head + count - 1;
                    if (back >= ring) back -= ring;
                    if (queueValue[back] < need) break;
                    count--;
                }
                int tail = head + count;
                if (tail >= ring) tail -= ring;
                queueIndex[tail] = k;
                queueValue[tail] = need;
                count++;
                float m = queueValue[head];

                // Скользящее среднее m по w + 1 последним значениям.
                sum += (double)m - minimums[slot];
                minimums[slot] = m;
                if (++slot == ring) slot = 0;

                // Окно среднего [t, t + w] для t = k − w собрано целиком: усиление отсчёта t готово.
                int t = k - w;
                if (t >= 0)
                {
                    float g = (float)(sum / ring);
                    if (g < minGain) minGain = g;
                    x[t] *= g;
                }
            }

            return minGain;
        }

        /// <summary>
        /// K-взвешивание из ITU-R BS.1770: два бикуада (ступенька выше ≈ 1,7 кГц и срез ниже ≈ 38 Гц). Коэффициенты считаются
        /// для любой частоты дискретизации; для 48 кГц совпадают с таблицей стандарта. Состояние хранится в double.
        /// </summary>
        private sealed class KWeighting
        {
            // Ступенька выше ≈ 1,7 кГц.
            private readonly double _b0;
            private readonly double _b1;
            private readonly double _b2;
            private readonly double _a1;
            private readonly double _a2;

            // Срез ниже ≈ 38 Гц (числитель 1, −2, 1).
            private readonly double _c1;
            private readonly double _c2;

            // Состояния двух фильтров (транспонированная форма II).
            private double _s1;
            private double _s2;
            private double _t1;
            private double _t2;

            public KWeighting(int sampleRate)
            {
                double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
                double k = Math.Tan(Math.PI * f0 / sampleRate);
                double vh = Math.Pow(10.0, gain / 20.0);
                double vb = Math.Pow(vh, 0.4996667741545416);
                double a0 = 1.0 + k / q + k * k;
                _b0 = (vh + vb * k / q + k * k) / a0;
                _b1 = 2.0 * (k * k - vh) / a0;
                _b2 = (vh - vb * k / q + k * k) / a0;
                _a1 = 2.0 * (k * k - 1.0) / a0;
                _a2 = (1.0 - k / q + k * k) / a0;

                f0 = 38.13547087602444;
                q = 0.5003270373238773;
                k = Math.Tan(Math.PI * f0 / sampleRate);
                a0 = 1.0 + k / q + k * k;
                _c1 = 2.0 * (k * k - 1.0) / a0;
                _c2 = (1.0 - k / q + k * k) / a0;
            }

            public double Process(double x)
            {
                double y = _b0 * x + _s1;
                _s1 = _b1 * x - _a1 * y + _s2;
                _s2 = _b2 * x - _a2 * y;

                double z = y + _t1;
                _t1 = -2.0 * y - _c1 * z + _t2;
                _t2 = y - _c2 * z;
                return z;
            }
        }
    }

    /// <summary>
    /// Огибающая мягкого гейта пауз (см. CachedSound). Строится один раз при декодировании, в фоновом потоке (линейное время, по оценке,
    /// а не по замеру: единицы миллисекунд на десять секунд звука); на горячем пути от неё остаётся одно умножение на отсчёт
    /// в CachedSoundProvider.
    ///
    /// Зачем. Тихую фразу приходится усиливать на 12 дБ и больше, и вместе с голосом поднимается шум записи: в паузах между словами
    /// слышен шип. Гейт приглушает именно паузы, и только у усиленных фраз (CachedSound.GateStrengthFor).
    ///
    /// Как не задеть речь. Звук известен заранее, поэтому у гейта есть упреждение, которого нет у живого:
    ///  • уровень — RMS в окне 20 мс по отсчётам файла, до усиления. Граница InertDb = −48 dBFS, у очень тихого файла ниже («громкость − 10 дБ»):
    ///    всё, что на границе и выше, включая тихие согласные, гейт не трогает вообще;
    ///  • «живым» считается наибольший уровень в окне от −80 мс (удержание: хвосты слов, смычки, дыхание) до +20 мс (упреждение: гейт
    ///    открывается до начала слова, а не после, и начало слова не срезается);
    ///  • ниже границы работает мягкий расширитель: колено 6 дБ, затем 1 дБ ослабления на каждый дБ уровня ниже границы, но не глубже
    ///    12 дБ. Шип −58 dBFS получает ≈ 7 дБ, −62 — ≈ 11 дБ, а пауза с шумом около −50 почти не меняется;
    ///  • скорость ограничена в обе стороны: закрытие на полную глубину за 120 мс (Release), открытие за 15 мс (Attack), причём открытие
    ///    идёт «назад по времени», то есть начинается заранее и к началу слова гейт уже открыт. Два прохода (вперёд для Release, назад
    ///    для Attack) дают наибольшую огибающую, которая нигде не закрыта сильнее, чем требует кривая расширителя.
    /// Результат — линейные множители ≈ 0,25…1 в узлах через 1 мс; проигрыватель интерполирует между узлами.
    /// </summary>
    internal static class PauseGate
    {
        /// <summary>Усиление выравнивания, дБ, до которого гейт выключен. Выше он включается и к FullGainDb действует в полную силу.</summary>
        public const float StartGainDb = 12f;
        public const float FullGainDb = 15f;

        private const int HopMs = 1;                     // шаг огибающей
        private const int WindowMs = 20;                 // окно измерения уровня, по центру шага (два периода голоса от 100 Гц)
        private const int HoldMs = 80;                   // удержание после «живого» уровня: хвосты слов, смычки согласных
        private const int LookaheadMs = 20;              // упреждение до «живого» уровня: начало слова не обрезается
        private const float InertDb = -48f;              // уровень, dBFS по отсчётам файла (до усиления), на котором и выше гейт не вмешивается
        private const float LoudnessMarginDb = 10f;      // и не выше «громкость − запас»: у очень тихого файла граница ниже
        private const float KneeDb = 6f;                 // плавный вход в расширитель вместо излома
        private const float Ratio = 2f;                  // каждый дБ уровня ниже границы даёт (Ratio − 1) дБ ослабления
        private const float MaxReductionDb = 12f;        // глубже гейт не закрывается
        private const float AttackMs = 15f;              // открытие на всю глубину (идёт назад по времени)
        private const float ReleaseMs = 120f;            // закрытие на всю глубину
        private const float MinUsefulReductionDb = 0.5f; // огибающая, нигде не закрывающаяся глубже, не нужна
        private const float ClosedDb = 3f;               // «закрыт» для журнала: ослабление не менее

        /// <summary>
        /// Строит огибающую. Возвращает null, если гейт не нужен: громкость не измерена (NaN), фраза короче двух шагов или пауз нет
        /// (ослабление нигде не достигает MinUsefulReductionDb). hop — расстояние между узлами в отсчётах; depthDb — наибольшее ослабление;
        /// closedFraction — доля времени с ослаблением не менее ClosedDb.
        /// </summary>
        public static float[]? Build(float[] x, int sampleRate, float loudnessDb, out int hop, out float depthDb, out float closedFraction)
        {
            hop = Math.Max(1, HopMs * sampleRate / 1000);
            depthDb = 0f;
            closedFraction = 0f;
            if (float.IsNaN(loudnessDb)) return null;

            int hops = x.Length / hop;
            if (hops < 2) return null;

            // 1. Уровень в децибелах для окна, центрированного на шаге: энергия по шагам и скользящая сумма (double: ошибка не копится).
            var cumulative = new double[hops + 1];
            for (int j = 0; j < hops; j++)
            {
                double energy = 0.0;
                int p = j * hop;
                for (int i = 0; i < hop; i++)
                {
                    double v = x[p + i];
                    energy += v * v;
                }
                cumulative[j + 1] = cumulative[j] + energy;
            }

            int window = Math.Max(1, WindowMs / HopMs);
            var level = new float[hops];
            for (int j = 0; j < hops; j++)
            {
                int lo = Math.Max(0, j - window / 2);
                int hi = Math.Min(hops, j - window / 2 + window);   // окно [lo, hi): у краёв файла короче, hi − lo ≥ 1
                double meanSquare = (cumulative[hi] - cumulative[lo]) / ((double)(hi - lo) * hop);
                level[j] = (float)(10.0 * Math.Log10(meanSquare + 1e-12));
            }

            // 2. «Живой» уровень шага j — наибольший уровень в окне [j − hold, j + look] (монотонная очередь в кольце: индексы окна, уровни
            //    убывают от головы к хвосту). Из него по кривой расширителя получается требуемое ослабление.
            float inert = Math.Min(InertDb, loudnessDb - LoudnessMarginDb);
            int hold = HoldMs / HopMs, look = LookaheadMs / HopMs;
            var reduction = new float[hops];
            int ring = hold + look + 1;
            var queue = new int[ring];
            int head = 0, count = 0;
            for (int r = 0; r < hops + look; r++)
            {
                int j = r - look;
                while (count > 0 && queue[head] < j - hold)          // сначала выбывают устаревшие индексы: кольцо не переполняется
                {
                    if (++head == ring) head = 0;
                    count--;
                }
                if (r < hops)
                {
                    while (count > 0)                                // новый уровень вытесняет с хвоста всё, что не выше его
                    {
                        int back = head + count - 1;
                        if (back >= ring) back -= ring;
                        if (level[queue[back]] > level[r]) break;
                        count--;
                    }
                    int tail = head + count;
                    if (tail >= ring) tail -= ring;
                    queue[tail] = r;
                    count++;
                }
                if (j >= 0) reduction[j] = ReductionFor(level[queue[head]], inert);   // очередь не пуста: индекс j ещё в окне
            }

            // 3. Скорости. Ослабление растёт (гейт закрывается) не быстрее releaseStep на шаг, а убывает (гейт открывается) не быстрее
            //    attackStep на шаг; открытие считается обратным проходом, поэтому ослабление перед словом спадает заранее.
            float releaseStep = MaxReductionDb * HopMs / ReleaseMs;
            float attackStep = MaxReductionDb * HopMs / AttackMs;
            for (int j = 1; j < hops; j++)
                reduction[j] = Math.Min(reduction[j], reduction[j - 1] + releaseStep);
            for (int j = hops - 2; j >= 0; j--)
                reduction[j] = Math.Min(reduction[j], reduction[j + 1] + attackStep);

            float deepest = 0f;
            int closed = 0;
            for (int j = 0; j < hops; j++)
            {
                if (reduction[j] > deepest) deepest = reduction[j];
                if (reduction[j] >= ClosedDb) closed++;
            }
            if (deepest < MinUsefulReductionDb) return null;   // гейт почти не закрывается: огибающая только занимала бы память

            // 4. Линейные множители. Два лишних узла в конце: проигрыватель берёт пару (j, j + 1), в том числе для хвоста короче шага.
            var envelope = new float[hops + 2];
            for (int j = 0; j < hops; j++)
                envelope[j] = MathF.Pow(10f, -reduction[j] / 20f);
            envelope[hops] = envelope[hops + 1] = envelope[hops - 1];

            depthDb = deepest;
            closedFraction = (float)closed / hops;
            return envelope;
        }

        // Ослабление (дБ, ≥ 0) для уровня levelDb при границе inert: на границе и выше 0; ниже — плавно в пределах колена, дальше
        // (Ratio − 1) дБ на каждый дБ ниже границы, но не больше MaxReductionDb. Кривая непрерывна: на конце колена обе ветви дают slope·Knee/2.
        private static float ReductionFor(float levelDb, float inert)
        {
            float below = inert - levelDb;
            if (below <= 0f) return 0f;

            float slope = Ratio - 1f;
            float reduction = below < KneeDb ? slope * below * below / (2f * KneeDb) : slope * (below - KneeDb / 2f);
            return Math.Min(reduction, MaxReductionDb);
        }
    }

    /// <summary>
    /// Дешёвый «курсор» по общему массиву: свой на каждое проигрывание, массив не копируется. Вдобавок:
    ///  • Gain — множитель (выровненная громкость, для наушников ещё и ползунок «для себя»), меняется из любого потока; к новому
    ///    значению звук переходит линейно в пределах одного блока, поэтому при движении ползунка нет «зиппера» и щелчков;
    ///  • GateStrength — сила гейта пауз 0…1 (огибающая из CachedSound.GateEnvelope, интерполяция между узлами); переход к новой силе
    ///    так же плавный в пределах блока; без огибающей или при нулевой силе гейт ничего не стоит;
    ///  • короткие вход и выход: файл, обрезанный по ненулевому отсчёту, иначе щёлкает в эфире (особенно после подъёма на десятки дБ);
    ///  • Stop(): вместо обрыва фраза затухает за StopFadeMs и заканчивается сама — микшер снимает источник, вернувший меньше запрошенного.
    /// </summary>
    public sealed class CachedSoundProvider : ISampleProvider
    {
        private const int FadeInMs = 3;      // не смазывает атаку согласных, но убирает щелчок на старте
        private const int FadeOutMs = 5;     // естественный конец файла
        private const int StopFadeMs = 15;   // прерывание: фраза уже не нужна, но обрыв короче периода голоса щёлкает

        private readonly float[] _samples;
        private readonly int _fadeIn;        // длины в отсчётах
        private readonly int _fadeOut;
        private readonly int _stopFade;
        private int _position;
        private readonly float[]? _gate;     // огибающая гейта пауз (узлы через _gateHop отсчётов) или null
        private readonly int _gateHop;
        private float _gain;                 // целевой множитель (пишет любой поток)
        private float _applied;              // множитель в конце прошлого блока (только поток вывода)
        private float _gateStrength;         // целевая сила гейта 0…1 (пишет любой поток)
        private float _gateApplied;          // сила гейта в конце прошлого блока (только поток вывода)
        private int _stopRequested;          // 1 — затухание запрошено
        private int _stopLeft = -1;          // сколько отсчётов затухания осталось, -1 — не начиналось (только поток вывода)

        public WaveFormat WaveFormat { get; }

        /// <summary>Фраза, которую играет курсор: по её пику и громкости пересчитываются Gain и GateStrength, когда меняют потолок на лету.</summary>
        public CachedSound Sound { get; }

        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, Math.Max(0f, value));
        }

        /// <summary>
        /// Сила гейта пауз, 0…1 (0 — выключен). Из любого потока. Если у фразы нет огибающей, всегда 0. Значение вне диапазона и NaN
        /// приводятся к 0…1 (NaN → 0).
        /// </summary>
        public float GateStrength
        {
            get => Volatile.Read(ref _gateStrength);
            set => Volatile.Write(ref _gateStrength, _gate == null || !(value > 0f) ? 0f : Math.Min(value, 1f));
        }

        public CachedSoundProvider(CachedSound sound, int sampleRate, float gain = 1f, float gateStrength = 0f)
        {
            Sound = sound;
            _samples = sound.Samples;
            _gate = sound.GateEnvelope;
            _gateHop = sound.GateHop;
            _gain = _applied = Math.Max(0f, gain);   // первый блок идёт сразу с нужным уровнем, без перехода от единицы
            _gateStrength = _gateApplied = _gate == null || !(gateStrength > 0f) ? 0f : Math.Min(gateStrength, 1f);
            _fadeIn = FadeInMs * sampleRate / 1000;
            _fadeOut = FadeOutMs * sampleRate / 1000;
            _stopFade = StopFadeMs * sampleRate / 1000;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        /// <summary>Из любого потока. Идемпотентна; на уже закончившийся источник не действует.</summary>
        public void Stop() => Volatile.Write(ref _stopRequested, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);

            if (_stopLeft < 0 && Volatile.Read(ref _stopRequested) != 0) _stopLeft = _stopFade;
            if (_stopLeft >= 0) n = Math.Min(n, _stopLeft);
            if (n <= 0) return 0;

            float g = _applied;
            float target = Volatile.Read(ref _gain);
            float step = (target - g) / n;
            int stopLeft = _stopLeft;
            int len = _samples.Length;
            int pos = _position;

            // Гейт пауз: сила переходит к новому значению так же линейно в пределах блока, как и Gain. Узел огибающей gj и смещение gp внутри шага
            // ведутся приращением (без деления на каждом отсчёте). Без огибающей или при силе 0 (и в начале, и в конце блока) ветка не включается.
            float[] env = _gate ?? Array.Empty<float>();
            float gs = _gateApplied;
            float gsTarget = _gate == null ? 0f : Volatile.Read(ref _gateStrength);
            float gsStep = (gsTarget - gs) / n;
            bool gateOn = _gate != null && (gs > 0f || gsTarget > 0f);
            int hop = _gateHop, gj = 0, gp = 0;
            float invHop = 0f;
            if (gateOn)
            {
                gj = pos / hop;
                gp = pos - gj * hop;
                invHop = 1f / hop;
            }

            for (int i = 0; i < n; i++, pos++)
            {
                g += step;
                float k = g;
                if (gateOn)
                {
                    gs += gsStep;
                    float e0 = env[gj];
                    float e = e0 + (env[gj + 1] - e0) * (gp * invHop);   // линейная интерполяция между узлами огибающей
                    k *= 1f - gs * (1f - e);
                    if (++gp == hop)
                    {
                        gp = 0;
                        gj++;
                    }
                }
                if (pos < _fadeIn) k *= (float)pos / _fadeIn;
                int toEnd = len - pos;                                    // ≥ 1
                if (toEnd <= _fadeOut) k *= (float)toEnd / _fadeOut;
                if (stopLeft >= 0) k *= (float)(stopLeft - i) / _stopFade;   // stopLeft - i ≥ 1
                buffer[offset + i] = _samples[pos] * k;
            }

            _position = pos;
            _applied = target;
            _gateApplied = gsTarget;
            if (stopLeft >= 0) _stopLeft = stopLeft - n;
            return n;
        }
    }

    /// <summary>
    /// LRU-кэш декодированных фраз с бюджетом по памяти (по умолчанию 96 МБ ≈ 8,7 мин звука 48 кГц mono float).
    /// На горячем пути нет ни диска, ни декодера. Ключ включает размер и время изменения файла,
    /// поэтому подменённый файл с тем же именем не звучит старой версией, и запас ReferenceCrestDb, с которым фраза подготовлена:
    /// при его смене записи с прежним запасом никогда не отдаются.
    /// </summary>
    public sealed class PhraseAudioCache
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<CachedSound>>> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trimLock = new();
        private readonly int _sampleRate;
        private readonly long _budgetBytes;
        private long _bytes;
        private float _referenceCrestDb = CachedSound.DefaultReferenceCrestDb;

        public PhraseAudioCache(int sampleRate, long budgetBytes = 96L << 20)
        {
            _sampleRate = sampleRate;
            _budgetBytes = budgetBytes;
        }

        /// <summary>
        /// Запас «пик над громкостью» (Settings.ReferenceCrestDb), с которым готовятся фразы; приводится к 8…24 дБ. При изменении кэш
        /// очищается: порог ограничителя зашит в декодированные отсчёты, пересчитать на лету их нельзя, и фразы готовятся заново.
        /// Играющие в этот момент фразы не страдают: провайдер держит свой массив.
        /// </summary>
        public float ReferenceCrestDb
        {
            get => Volatile.Read(ref _referenceCrestDb);
            set
            {
                float crest = CachedSound.ClampReferenceCrest(value);
                if (Interlocked.Exchange(ref _referenceCrestDb, crest) != crest) Clear();
            }
        }

        public async Task<CachedSound> GetAsync(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Файл фразы не найден", path);

            float crest = ReferenceCrestDb;   // один раз на вызов: ключ и декодирование должны использовать одно и то же значение
            string key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{crest:F2}";
            var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<CachedSound>>(() => Task.Run(() => new CachedSound(path, _sampleRate, crest))));

            try
            {
                CachedSound sound = await lazy.Value.ConfigureAwait(false);
                if (Interlocked.Exchange(ref sound.LastUsed, Environment.TickCount64 | 1) == 0)
                {
                    // Запись могли убрать (Clear при смене запаса), пока фраза декодировалась: тогда в бюджет она не входит.
                    if (_items.TryGetValue(key, out var current) && ReferenceEquals(current, lazy))
                    {
                        Interlocked.Add(ref _bytes, sound.Bytes);
                        Trim();
                    }
                }
                return sound;
            }
            catch
            {
                // Битый файл не должен навсегда остаться в кэше.
                _items.TryRemove(new KeyValuePair<string, Lazy<Task<CachedSound>>>(key, lazy));
                throw;
            }
        }

        /// <summary>Прогрев без ожидания результата. Ошибки декодирования игнорируются: решение «играть» принимается позже.</summary>
        public void Prefetch(string path) => _ = PrefetchAsync(path);

        private async Task PrefetchAsync(string path)
        {
            try
            {
                await GetAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cache] Prefetch: {ex.Message}");
            }
        }

        public void Clear()
        {
            _items.Clear();
            Interlocked.Exchange(ref _bytes, 0);
        }

        // Вытесняет самые давние записи, пока не уложимся в бюджет. Играющий провайдер держит массив сам, поэтому вытеснение ему не мешает.
        private void Trim()
        {
            if (Interlocked.Read(ref _bytes) <= _budgetBytes) return;

            lock (_trimLock)
            {
                var candidates = _items
                    .Where(kv => kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully)
                    .Select(kv => (kv.Key, Sound: kv.Value.Value.Result))
                    .Where(x => x.Sound.LastUsed != 0)           // в _bytes входят только уже учтённые фразы
                    .OrderBy(x => x.Sound.LastUsed)
                    .ToList();

                foreach (var candidate in candidates)
                {
                    if (Interlocked.Read(ref _bytes) <= _budgetBytes) break;
                    if (_items.TryRemove(candidate.Key, out _))
                        Interlocked.Add(ref _bytes, -candidate.Sound.Bytes);
                }
            }
        }
    }
}
