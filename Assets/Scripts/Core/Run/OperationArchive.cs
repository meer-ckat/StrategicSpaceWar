using System.IO;
using UnityEngine;

/// <summary>
/// 작전 기록. 잔해에서 회수한 단서가 순서대로 쌓인다(STORY.md ACT II의 사슬). **함장이 죽어도 안 지워진다** -
/// "정보/작전은 남는다"가 이 파일 하나다. 그래서 RunState(런 단위, 죽으면 지움)와 파일이 다르다.
/// 글은 StreamingAssets/Run/archive.json, 진도는 persistentDataPath/archive-progress.json.
/// </summary>
public static class OperationArchive
{
    [System.Serializable] private class Entry { public string[] lines; }
    [System.Serializable] private class Book { public Entry[] entries; }
    [System.Serializable] private class Progress { public int found; }

    private static Book _book;
    private static Progress _progress;

    private static string ProgressPath => Path.Combine(Application.persistentDataPath, "archive-progress.json");

    public static int Total => Load().entries?.Length ?? 0;

    public static int Found
    {
        get
        {
            if (_progress == null)
            {
                _progress = new Progress();
                if (File.Exists(ProgressPath))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(ProgressPath), _progress);
            }
            return _progress.found;
        }
    }

    /// <summary>다음 단서 하나를 풀고 무전으로 띄운다. 다 풀었으면 false - 잔해는 그냥 잔해다.</summary>
    public static bool Recover()
    {
        Book book = Load();
        int n = Found;
        if (book.entries == null || n >= book.entries.Length)
            return false;

        _progress.found = n + 1;
        File.WriteAllText(ProgressPath, JsonUtility.ToJson(_progress));

        string text = string.Join("\n", book.entries[n].lines);
        DialogueManager.current?.Spawn(text, $"작전 기록 {n + 1}/{book.entries.Length}", duration: 8f, style: "radio");
        Debug.Log($"[Archive] 단서 {n + 1}/{book.entries.Length} 회수.");
        return true;
    }

    private static Book Load()
    {
        if (_book != null)
            return _book;

        string path = Path.Combine(Application.streamingAssetsPath, "Run", "archive.json");
        _book = File.Exists(path) ? JsonUtility.FromJson<Book>(File.ReadAllText(path)) : new Book();
        return _book;
    }
}
