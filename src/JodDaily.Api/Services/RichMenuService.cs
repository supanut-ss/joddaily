using Microsoft.Extensions.Options;
using JodDaily.Config;
using JodDaily.Line;

namespace JodDaily.Services;

/// <summary>The 6-tile rich menu (3 columns x 2 rows) and the code that installs it as the default menu.</summary>
public static class RichMenuDefinition
{
    public const string Name = "JodDaily main menu";
    public const int Width = 2500, Height = 1686;

    /// <summary>Tiles left to right, top to bottom; must stay in the same order as tools/richmenu/menu.html.</summary>
    public static object Build(string liffId)
    {
        var liff = "https://liff.line.me/" + liffId;
        var actions = new object[]
        {
            Uri("จดรายจ่าย", liff + "?tab=new"),
            Uri("จดรายรับ", liff + "?tab=new&kind=income"),
            new { type = "message", label = "สรุปเดือนนี้", text = "สรุป" },
            Uri("ประวัติ", liff + "?tab=history"),
            Uri("ค่าสมาชิก", liff + "?tab=subs"),
            Uri("งบประมาณ", liff + "?tab=budget")
        };
        int w = Width / 3, h = Height / 2;
        return new
        {
            size = new { width = Width, height = Height },
            selected = true,
            name = Name,
            chatBarText = "เมนู จดจ่าย",
            areas = actions.Select((action, i) => new
            {
                bounds = new { x = (i % 3) * w, y = (i / 3) * h, width = i % 3 == 2 ? Width - 2 * w : w, height = i / 3 == 1 ? Height - h : h },
                action
            }).ToArray()
        };
    }

    private static object Uri(string label, string uri) => new { type = "uri", label, uri };
}

public class RichMenuService(LineClient line, IOptions<AppSettings> options, IWebHostEnvironment env)
{
    public record Result(bool Ok, string Message, string? RichMenuId = null);

    /// <summary>Creates the menu, uploads its image, makes it the default and removes earlier copies of it.</summary>
    public async Task<Result> InstallAsync()
    {
        var liffId = options.Value.Liff.Id;
        if (string.IsNullOrEmpty(liffId)) return new Result(false, "Liff:Id is not configured");
        var imagePath = Path.Combine(env.ContentRootPath, "richmenu", "richmenu.png");
        if (!File.Exists(imagePath)) return new Result(false, "richmenu/richmenu.png is missing (run tools/richmenu/render.ps1)");

        var old = (await line.ListRichMenusAsync()).Where(m => m.Name == RichMenuDefinition.Name).Select(m => m.Id).ToList();

        var id = await line.CreateRichMenuAsync(RichMenuDefinition.Build(liffId));
        if (id == null) return new Result(false, "LINE refused the rich menu definition");
        if (!await line.UploadRichMenuImageAsync(id, await File.ReadAllBytesAsync(imagePath)))
        {
            await line.DeleteRichMenuAsync(id);
            return new Result(false, "LINE refused the rich menu image");
        }
        if (!await line.SetDefaultRichMenuAsync(id)) return new Result(false, "Created and uploaded, but could not set it as the default menu", id);

        foreach (var o in old) await line.DeleteRichMenuAsync(o);   // only after the new one is live, so users never see no menu
        return new Result(true, old.Count == 0 ? "Rich menu installed" : $"Rich menu installed, {old.Count} older copy removed", id);
    }
}
