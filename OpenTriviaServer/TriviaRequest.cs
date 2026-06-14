using System.Net;

public class TriviaRequest
{
    public int Amount { get; set; } = 10;

    public string Type { get; set; } = "multiple";

    public HttpListenerContext Context { get; set; } = null!;
}
