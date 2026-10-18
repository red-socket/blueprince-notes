using System;
using System.Text.Json.Serialization;

namespace BluePrinceNotes.Notes;

/// <summary>One note. The first line of its text is its title.</summary>
public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; }   // UTC
    public DateTime Edited { get; set; }    // UTC
    public string Text { get; set; } = "";
    public NoteContext Context { get; set; } = new();

    /// <summary>The first line that isn't blank, trimmed; "" for an empty note.</summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            var t = Text ?? "";
            int start = 0;
            while (start < t.Length)
            {
                int end = t.IndexOf('\n', start);
                if (end < 0) end = t.Length;
                var line = t.Substring(start, end - start).Trim();
                if (line.Length > 0) return line;
                start = end + 1;
            }
            return "";
        }
    }

    [JsonIgnore]
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Where and when in the game a note was started. What the game couldn't tell keeps its default.</summary>
public sealed class NoteContext
{
    public int Day { get; set; }                // 0 = unknown
    public string Date { get; set; } = "";      // in-game calendar date, yyyy-MM-dd
    public int Minute { get; set; } = -1;       // in-game time, minutes after midnight
    public string Area { get; set; } = "";      // House, Grounds or Underground
    public string Room { get; set; } = "";      // room or outdoor area, as the HUD shows it
    public int Rank { get; set; }               // house only
    public int Tile { get; set; }               // house only: 1-46, 1 = west end of rank 1
    public int SaveSlot { get; set; }           // 1-4
    public int Seed { get; set; }               // the run's random seed, new on every Day 1
    public double DayStartClock { get; set; } = -1;  // total saved play time when the day started, seconds
}
