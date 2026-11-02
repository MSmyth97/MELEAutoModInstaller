using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mele.Engine.Stage;

namespace Mele.Engine.Textures;

/// <summary>Writes an M3 Batch Mod Installer group (.biq2) for a game's staged M3TO mods (ALOT, ISL), in guide order.</summary>
public static class M3toBatchWriter
{
    public static string Write(TextureManifest manifest, string game, string library, string outDir, DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.Now).ToString("o");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"{game} Textures M3TO (generated).biq2");

        using var stream = File.Create(path);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\r\n" }))
        {
            w.WriteStartObject();
            w.WriteString("game", game);
            w.WriteString("description", $"{game} M3TO textures (ALOT, ISL) - generated from guide 1127");
            w.WriteBoolean("restorebeforeinstall", false);
            w.WriteStartArray("mods");
            foreach (var item in manifest.Items(game).Where(i => i.Kind == TextureKind.M3to))
            {
                var ini = Path.Combine(library, game, item.Name, "moddesc.ini");
                if (!File.Exists(ini)) throw new StageException($"{item.Name}: not staged in the library ({ini})");
                var data = File.ReadAllBytes(ini);
                var modName = Regex.Match(System.Text.Encoding.UTF8.GetString(data), @"^\s*modname\s*=\s*(.+?)\s*$",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : item.Name;
                var link = $"https://nexusmods.com/{manifest.GameDomain}/mods/{item.ModId ?? manifest.Torrent.ModId}?tab=files"
                           + (item.FileId is { } f ? $"&file_id={f}" : "");

                w.WriteStartObject();
                w.WriteString("moddescpath", $"{game}\\{item.Name}\\moddesc.ini");
                w.WriteString("configurationtime", stamp);
                w.WriteString("moddeschash", Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant());
                w.WriteNumber("moddescsize", data.Length);
                w.WriteString("downloadlink", link);
                w.WriteStartArray("userchosenoptions"); w.WriteEndArray();
                w.WriteStartArray("allchosenoptions"); w.WriteEndArray();
                w.WriteBoolean("haschosenoptions", true);
                w.WriteBoolean("IsStandalone", true);
                w.WriteString("modname", modName);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return path;
    }
}
