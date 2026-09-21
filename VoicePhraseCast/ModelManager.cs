using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VoicePhraseCast
{
    public class ModelManager
    {
        private readonly string _modelsDirectory;
        private readonly string _legacyModelDirectory;

        public ModelManager()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelsDirectory = Path.Combine(baseDir, "models");
            _legacyModelDirectory = Path.Combine(baseDir, "model");

            InitializeDirectoriesAndMigrate();
        }

        /// <summary>
        /// Создает целевую папку models/ и выполняет миграцию старой папки model/, если она существует и валидна.
        /// </summary>
        private void InitializeDirectoriesAndMigrate()
        {
            try
            {
                if (!Directory.Exists(_modelsDirectory))
                {
                    Directory.CreateDirectory(_modelsDirectory);
                }

                // Проверяем существование legacy-папки model/
                if (Directory.Exists(_legacyModelDirectory))
                {
                    if (IsValidModelFolder(_legacyModelDirectory))
                    {
                        string targetMigrationPath = Path.Combine(_modelsDirectory, "vosk-model-small-ru-0.22");

                        // Переносим только если целевая папка еще не существует
                        if (!Directory.Exists(targetMigrationPath))
                        {
                            Directory.Move(_legacyModelDirectory, targetMigrationPath);
                            Debug.WriteLine($"[ModelManager] Легаси модель успешно перенесена в: {targetMigrationPath}");
                        }
                        else
                        {
                            // Если новая папка уже есть, просто очищаем старую легаси-директорию
                            Directory.Delete(_legacyModelDirectory, true);
                            Debug.WriteLine("[ModelManager] Найдена дублирующая легаси папка model/. Старая папка удалена.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager Migration Error] {ex.Message}");
            }
        }

        /// <summary>
        /// Сканирует папку models/ и возвращает список путей ко всем валидным моделям Vosk.
        /// </summary>
        public List<string> GetAvailableModels()
        {
            var validModels = new List<string>();

            if (!Directory.Exists(_modelsDirectory))
                return validModels;

            try
            {
                var subdirectories = Directory.GetDirectories(_modelsDirectory);

                foreach (var dir in subdirectories)
                {
                    if (IsValidModelFolder(dir))
                    {
                        validModels.Add(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelManager Scan Error] {ex.Message}");
            }

            return validModels;
        }

        /// <summary>
        /// Проверяет, содержит ли указанная директория обязательные составляющие Vosk (папки conf и am).
        /// </summary>
        public bool IsValidModelFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            string confPath = Path.Combine(folderPath, "conf");
            string amPath = Path.Combine(folderPath, "am");

            return Directory.Exists(confPath) && Directory.Exists(amPath);
        }

        public async Task<string?> EnsureModelAvailableAsync(Action<string>? statusCallback = null)
        {
            var available = GetAvailableModels();

            // 1. Проверяем сохраненный путь в Settings
            string savedPath = Properties.Settings.Default.SelectedModelPath;
            if (!string.IsNullOrWhiteSpace(savedPath) && IsValidModelFolder(savedPath))
            {
                return savedPath;
            }

            // 2. Если в настройках ничего нет, но есть хотя бы одна валидная модель в models/
            if (available.Count > 0)
            {
                string defaultPath = available[0];
                Properties.Settings.Default.SelectedModelPath = defaultPath;
                Properties.Settings.Default.Save();
                return defaultPath;
            }

            // 3. Если моделей нет вообще — скачиваем дефолтную
            string targetFolder = Path.Combine(_modelsDirectory, "vosk-model-small-ru-0.22");
            await DownloadDefaultModelAsync(targetFolder, statusCallback);

            if (IsValidModelFolder(targetFolder))
            {
                Properties.Settings.Default.SelectedModelPath = targetFolder;
                Properties.Settings.Default.Save();
                return targetFolder;
            }

            return null;
        }

        private async Task DownloadDefaultModelAsync(string targetFolder, Action<string>? statusCallback)
        {
            string zipPath = Path.Combine(_modelsDirectory, "vosk_model.zip");
            string tempExtractPath = Path.Combine(_modelsDirectory, "temp_model");
            const string url = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";

            statusCallback?.Invoke("Модели отсутствуют. Скачивание бакаут-модели (около 50 МБ)...");

            using (var client = new System.Net.Http.HttpClient())
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }
            }

            statusCallback?.Invoke("Распаковка модели ИИ...");

            if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempExtractPath);

            string innerFolder = Directory.GetDirectories(tempExtractPath)[0];
            if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, true);
            Directory.Move(innerFolder, targetFolder);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
        }
    }
}