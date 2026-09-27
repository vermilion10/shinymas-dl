using System.Text;
using System.Text.Json;

namespace ShinymasDl.Core.Extraction;

/// <summary> a readable script.txt beside each story script: speaker, line, choices, and the voice file </summary>
public static class Transcript
{
    public static bool TryWrite(byte[] scriptJson, string destination)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(scriptJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var builder = new StringBuilder();
            foreach (var line in document.RootElement.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Text(line, "text") is { } text)
                {
                    var speaker = Text(line, "speaker") ?? "";
                    builder.Append(speaker).Append(": ").Append(Indent(text));

                    // "produce_events/101200101/1902001010010" plays voice/1902001010010.m4a
                    if (Text(line, "voice") is { } voice)
                    {
                        builder.Append("  [voice/").Append(voice[(voice.LastIndexOf('/') + 1)..]).Append(".m4a]");
                    }

                    builder.AppendLine();
                }

                if (Text(line, "select") is { } choice)
                {
                    builder.Append("  > ").AppendLine(Indent(choice));
                }
            }

            if (builder.Length == 0)
            {
                return false;
            }

            File.WriteAllText(destination, builder.ToString());
            return true;
        }
    }

    private static string Indent(string text) => text.ReplaceLineEndings("\n    ");

    private static string? Text(JsonElement line, string property) =>
        line.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
