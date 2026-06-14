using System.Net;
using System.Text;
using System.Text.Json;

class Program
{
    private const int MaxParallelHandlers = 5;
    private const int CacheMaxSize = 5;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(120);

    private static readonly Queue<TriviaRequest> requestQueue = new();
    private static readonly object queueLock = new();

    private static readonly SizeLimitedCache cache = new(CacheMaxSize, CacheDuration);

    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly object stampedeLock = new();
    private static readonly Dictionary<string, Task<string>> requestsInProgress = new();

    private static bool isRunning = true;

    static async Task Main(string[] args)
    {
        HttpListener listener = new();
        listener.Prefixes.Add("http://localhost:8080/");
        listener.Start();

        Thread escListenerThread = new Thread(() => ListenForEsc(listener));
        escListenerThread.IsBackground = true;
        escListenerThread.Start();

        ThreadSafeLogger.Info("Server pokrenut na adresi: http://localhost:8080/");
        ThreadSafeLogger.Info("Primer poziva: http://localhost:8080/search?amount=10&type=multiple");
        ThreadSafeLogger.Info("Pritisni ESC za zaustavljanje servera.\n");

        List<Task> workerTasks = new List<Task>();

        for (int i = 1; i <= MaxParallelHandlers; i++)
        {
            int workerId = i;
            Task workerTask = Task.Run(() => ProcessRequestsAsync(workerId));
            workerTasks.Add(workerTask);
        }

        try
        {
            await AcceptRequestsAsync(listener);
        }
        finally
        {
            isRunning = false;

            try
            {
                await Task.WhenAll(workerTasks);
            }
            catch
            {
            }

            listener.Close();
            ThreadSafeLogger.Info("Server je zaustavljen.");
        }
    }

    private static void ListenForEsc(HttpListener listener)
    {
        while (isRunning)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.Escape)
            {
                ThreadSafeLogger.Info("Zaustavljanje servera...");
                isRunning = false;

                try
                {
                    listener.Stop();
                }
                catch
                {
                }

                return;
            }
        }
    }

    private static async Task AcceptRequestsAsync(HttpListener listener)
    {
        while (isRunning)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            string path = context.Request.Url?.AbsolutePath ?? "";

            if (path != "/search")
            {
                await SendResponseAsync(
                    context,
                    "Putanja nije dobra. Koristi /search?amount=10&type=multiple.",
                    "text/plain",
                    404
                );
                continue;
            }

            int amount;
            string amountText = context.Request.QueryString["amount"] ?? "10";

            try
            {
                amount = int.Parse(amountText);
            }
            catch
            {
                await SendResponseAsync(context, "Parametar amount mora biti broj.", "text/plain", 400);
                continue;
            }

            if (amount < 1 || amount > 50)
            {
                await SendResponseAsync(context, "Parametar amount mora biti od 1 do 50.", "text/plain", 400);
                continue;
            }

            string type = context.Request.QueryString["type"] ?? "multiple";
            type = type.ToLower();

            if (type != "multiple" && type != "boolean")
            {
                await SendResponseAsync(context, "Parametar type mora biti multiple ili boolean.", "text/plain", 400);
                continue;
            }

            TriviaRequest request = new TriviaRequest
            {
                Amount = amount,
                Type = type,
                Context = context
            };

            lock (queueLock)
            {
                requestQueue.Enqueue(request);
            }

            ThreadSafeLogger.Info($"[QUEUE] Dodat zahtev: {BuildCacheKey(request)}");
        }
    }

    private static async Task ProcessRequestsAsync(int workerId)
    {
        while (isRunning)
        {
            TriviaRequest? request = null;

            try
            {
                lock (queueLock)
                {
                    if (requestQueue.Count > 0)
                    {
                        request = requestQueue.Dequeue();
                    }
                }

                if (request == null)
                {
                    await Task.Delay(50);
                    continue;
                }

                ThreadSafeLogger.Info($"[WORKER {workerId}] Preuzet zahtev: {BuildCacheKey(request)}");
                await HandleRequestAsync(request, workerId);
            }
            catch (Exception ex)
            {
                ThreadSafeLogger.Info($"[ERROR] Worker {workerId}: {ex.Message}");

                if (request != null)
                {
                    await SafeSendResponseAsync(request.Context, "Doslo je do greske na serveru.", "text/plain", 500);
                }
            }
        }
    }

    private static async Task HandleRequestAsync(TriviaRequest request, int workerId)
    {
        string key = BuildCacheKey(request);

        string? cachedData = cache.Get(key);

        if (cachedData != null)
        {
            ThreadSafeLogger.Info($"[CACHE HIT] {key}");
            await SendResponseAsync(request.Context, cachedData, "application/json", 200);
            return;
        }

        string apiUrl = BuildOpenTriviaApiUrl(request);
        Task<string> apiTask = GetApiTask(key, apiUrl);

        try
        {
            string result = await apiTask;

            ThreadSafeLogger.Info($"[WORKER {workerId}] Salje odgovor: {key}");
            await SendResponseAsync(request.Context, result, "application/json", 200);
        }
        catch (TriviaDataUnavailableException ex)
        {
            await SendResponseAsync(request.Context, ex.Message, "text/plain", 400);
        }
        catch (TaskCanceledException)
        {
            await SendResponseAsync(request.Context, "Open Trivia API nije odgovorio na vreme.", "text/plain", 504);
        }
        catch (HttpRequestException)
        {
            await SendResponseAsync(request.Context, "Greska pri komunikaciji sa Open Trivia API servisom.", "text/plain", 502);
        }
    }

    private static Task<string> GetApiTask(string key, string apiUrl)
    {
        lock (stampedeLock)
        {
            if (requestsInProgress.ContainsKey(key))
            {
                ThreadSafeLogger.Info($"[CACHE WAIT] Vec postoji task za: {key}");
                return requestsInProgress[key];
            }

            ThreadSafeLogger.Info($"[API TASK START] {key}");

            Task<string> newTask = FetchFromApiAsync(apiUrl);
            requestsInProgress[key] = newTask;

            _ = newTask.ContinueWith(completedTask =>
            {
                if (completedTask.IsCompletedSuccessfully)
                {
                    cache.Put(key, completedTask.Result);
                    ThreadSafeLogger.Info($"[CONTINUE WITH] Rezultat upisan u kes: {key}");
                }
                else
                {
                    string error = "Nepoznata greska";

                    if (completedTask.Exception != null)
                    {
                        error = completedTask.Exception.GetBaseException().Message;
                    }

                    ThreadSafeLogger.Info($"[CONTINUE WITH] API task neuspesan za {key}: {error}");
                }

                lock (stampedeLock)
                {
                    requestsInProgress.Remove(key);
                }
            });

            return newTask;
        }
    }

    private static async Task<string> FetchFromApiAsync(string apiUrl)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(apiUrl);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Open Trivia API vratio status {(int)response.StatusCode}.");
        }

        string result = await response.Content.ReadAsStringAsync();

        TriviaApiResponse? apiResponse = JsonSerializer.Deserialize<TriviaApiResponse>(
            result,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        );

        if (apiResponse == null)
        {
            throw new TriviaDataUnavailableException("API odgovor nije u ispravnom JSON formatu.");
        }

        if (apiResponse.response_code != 0 || apiResponse.results.Count == 0)
        {
            throw new TriviaDataUnavailableException("Ne postoje pitanja za zadati zahtev.");
        }

        return result;
    }

    private static string BuildCacheKey(TriviaRequest request)
    {
        return $"amount={request.Amount}&type={request.Type}";
    }

    private static string BuildOpenTriviaApiUrl(TriviaRequest request)
    {
        string amount = request.Amount.ToString();
        string type = WebUtility.UrlEncode(request.Type);

        return $"https://opentdb.com/api.php?amount={amount}&type={type}";
    }

    private static async Task SendResponseAsync(
        HttpListenerContext context,
        string text,
        string contentType,
        int statusCode
    )
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentEncoding = Encoding.UTF8;
        context.Response.ContentLength64 = bytes.Length;

        try
        {
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        finally
        {
            context.Response.OutputStream.Close();
            context.Response.Close();
        }
    }

    private static async Task SafeSendResponseAsync(
        HttpListenerContext context,
        string text,
        string contentType,
        int statusCode
    )
    {
        try
        {
            await SendResponseAsync(context, text, contentType, statusCode);
        }
        catch
        {
        }
    }
}
