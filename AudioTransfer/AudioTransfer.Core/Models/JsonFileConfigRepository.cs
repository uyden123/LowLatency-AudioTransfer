using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.Core.Models
{
    public class JsonFileConfigRepository<T> : IConfigRepository<T> where T : class, new()
    {
        private readonly string _filePath;

        public JsonFileConfigRepository(string fileNameOrPath)
        {
            _filePath = Path.IsPathRooted(fileNameOrPath) 
                ? fileNameOrPath 
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileNameOrPath);
        }

        public async Task<T> LoadOrDefaultAsync()
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var config = JsonSerializer.Deserialize<T>(json, options) ?? new T();
                    CoreLogger.Instance.Log($"[Config] Loaded {typeof(T).Name} from {_filePath}");
                    return config;
                }
                catch (Exception ex)
                {
                    CoreLogger.Instance.Log($"[Config] Load error for {typeof(T).Name}: {ex.Message}");
                    return new T();
                }
            }
            return new T();
        }

        public async Task SaveAsync(T config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json);
                CoreLogger.Instance.Log($"[Config] Saved {typeof(T).Name} to {_filePath}");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"[Config] Save error for {typeof(T).Name}: {ex.Message}");
            }
        }
    }
}
