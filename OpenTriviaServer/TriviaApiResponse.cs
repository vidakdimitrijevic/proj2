public class TriviaApiResponse
{
    public int response_code { get; set; }

    public List<Question> results { get; set; } = new();
}

public class Question
{
    public string question { get; set; } = "";

    public string correct_answer { get; set; } = "";

    public List<string> incorrect_answers { get; set; } = new();
}
