using System.Diagnostics;
using System.Threading.Tasks;

public static class YtDlpHelper
{
    public static bool IsValidYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Простая и эффективная проверка в стиле r-стратега
        return url.Contains("youtube.com/watch?v=") ||
               url.Contains("youtu.be/") ||
               url.Contains("youtube.com/shorts/");
    }
    public static async Task DownloadVideoAsync(string url, string outputPath)
    {
        // Проверка URL перед запуском процесса
        if (!IsValidYouTubeUrl(url))
        {
            throw new ArgumentException("Попытка загрузки некорректного YouTube URL.");
        }

        // Обновление yt-dlp перед загрузкой
        await RunProcessAsync("yt-dlp", "-U");

        // Флаг --force-overwrites позволяет заменить старый файл новым, 
        // если пользователь решил прикрепить другое видео к тому же треку.
        string args = $"--force-overwrites --no-playlist --socket-timeout 15 --retries 10 --fragment-retries 10 -f \"bestvideo[width<=640][ext=mp4]+bestaudio[ext=m4a]/best[width<=640]/best\" -o \"{outputPath}\" \"{url}\"";
        await RunProcessAsync("yt-dlp", args);
    }

    private static Task RunProcessAsync(string fileName, string arguments)
    {
        var tcs = new TaskCompletionSource();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true, // Никаких всплывающих консолей
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        process.Exited += (s, e) =>
        {
            process.Dispose();
            tcs.SetResult();
        };

        process.Start();
        return tcs.Task;
    }

    // Добавь это внутрь YtDlpHelper:
    public static async Task<string> GetDirectStreamUrlAsync(string url)
    {
        if (!IsValidYouTubeUrl(url))
        {
            System.Diagnostics.Debug.WriteLine($"[YtDlp] Отклонено: Невалидный URL - {url}");
            return null;
        }

        return await Task.Run(() =>
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    // Ищем лучший цельный поток с видео и аудио
                    Arguments = $"--socket-timeout 5 --geo-bypass -f \"best[width<=640]/best\" -g --no-playlist \"{url}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            // Забираем первую строку (прямую ссылку)
            string output = process.StandardOutput.ReadLine()?.Trim();
            process.WaitForExit();
            return output;
        });
    }
}