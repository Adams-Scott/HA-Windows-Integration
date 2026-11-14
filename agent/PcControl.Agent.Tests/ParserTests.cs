using PcControl.Agent.Api;
using PcControl.Agent.Checks;
using PcControl.Agent.Config;
using PcControl.Agent.Entities;
using PcControl.Agent.Scripts;
using PcControl.Agent.Steam;

namespace PcControl.Agent.Tests;

public class ScriptHeaderTests
{
    [Fact]
    public void ParsesLineCommentHeaders()
    {
        var h = ScriptHeaderParser.Parse([
            "# HA-Name: Plex Server",
            "#ha-check:   process:Plex Media Server.exe  ",
            "  #  HA-Icon : mdi:plex",
            "param([string]$State)",
        ]);
        Assert.Equal("Plex Server", h["Name"]);
        Assert.Equal("process:Plex Media Server.exe", h["check"]);
        Assert.Equal("mdi:plex", h["ICON"]);
    }

    [Fact]
    public void ParsesBlockCommentHeaders()
    {
        var h = ScriptHeaderParser.Parse([
            "<#",
            "  HA-Name: VPN",
            "  # HA-Icon: mdi:vpn",
            "#>",
            "HA-Check: process:not-a-comment",
        ]);
        Assert.Equal("VPN", h["Name"]);
        Assert.Equal("mdi:vpn", h["Icon"]);
        Assert.False(h.ContainsKey("Check"));
    }

    [Fact]
    public void ParsesSingleLineBlock()
    {
        var h = ScriptHeaderParser.Parse(["<# HA-Name: One Liner #>"]);
        Assert.Equal("One Liner", h["Name"]);
    }

    [Fact]
    public void IgnoresLinesAfter30()
    {
        var lines = Enumerable.Repeat("# filler", 30).Append("# HA-Name: Late").ToList();
        Assert.Empty(ScriptHeaderParser.Parse(lines));
    }

    [Theory]
    [InlineData("Plex.ps1", "plex")]
    [InlineData("Plex Server.PS1", "plex_server")]
    [InlineData("my-vpn (work).ps1", "my_vpn__work_")]
    public void Slugs(string file, string slug) => Assert.Equal(slug, Slug.FromFileName(file));

    [Fact]
    public void BuildsEntityWithDefaults()
    {
        var e = ScriptScanner.FromHeaders(@"C:\s\My VPN.ps1", new Dictionary<string, string>())!;
        Assert.Equal("script.my_vpn", e.Id);
        Assert.Equal("My VPN", e.Name);
        Assert.Equal("mdi:application", e.Icon);
        Assert.Equal(EntityStates.Assumed, e.State);
    }

    [Fact]
    public void SkipsDisabled()
    {
        Assert.Null(ScriptScanner.FromHeaders(@"C:\s\x.ps1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Disabled"] = "true" }));
    }
}

public class CheckSpecTests
{
    [Fact]
    public void ProcessWithSeveralNames()
    {
        var c = CheckSpec.Parse("process: Plex Media Server.exe, plexscanner ,");
        Assert.Equal(CheckType.Process, c.Type);
        Assert.Equal(["Plex Media Server", "plexscanner"], c.ProcessNames);
    }

    [Fact]
    public void Port()
    {
        Assert.Equal(32400, CheckSpec.Parse("PORT:32400").Port);
        Assert.Equal(CheckType.Invalid, CheckSpec.Parse("port:abc").Type);
    }

    [Theory]
    [InlineData("service:Spooler", CheckType.Service)]
    [InlineData("window:Notepad", CheckType.Window)]
    [InlineData("ps:Test-Path C:\\x", CheckType.PowerShell)]
    [InlineData("bogus:1", CheckType.Invalid)]
    [InlineData("nocolon", CheckType.Invalid)]
    public void Types(string raw, CheckType type) => Assert.Equal(type, CheckSpec.Parse(raw).Type);
}

public class VdfTests
{
    [Fact]
    public void ParsesNewLibraryFolders()
    {
        var text = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "123" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"   // comment
                }
            }
            """;
        var root = VdfParser.Parse(text);
        var folders = root.Obj("libraryfolders")!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.Obj("0")!.Str("path"));
        Assert.Equal(@"D:\SteamLibrary", folders.Obj("1")!.Str("PATH"));
    }

    [Fact]
    public void ReadsLibraryFoldersFromDisk()
    {
        var steam = Path.Combine(Path.GetTempPath(), "pcc-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        try
        {
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                $$"""
                "libraryfolders" { "0" { "path" "{{steam.Replace("\\", "\\\\")}}" } "1" { "path" "E:\\Games" } }
                """);
            var libs = SteamScanner.ReadLibraryFolders(steam);
            Assert.Equal(2, libs.Count);
            Assert.Equal(@"E:\Games", libs[1]);
        }
        finally
        {
            Directory.Delete(steam, true);
        }
    }

    [Fact]
    public void ParsesOldLibraryFolders()
    {
        var root = VdfParser.Parse("\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"1\" \"D:\\\\Steam\" }");
        Assert.Equal(@"D:\Steam", root.Obj("libraryfolders")!.Str("1"));
    }

    [Fact]
    public void ParsesManifest()
    {
        var acf = """
            "AppState"
            {
                "appid"		"620"
                "Universe"		"1"
                "name"		"Portal 2"
                "installdir"		"Portal 2"
                "UserConfig" { "language" "english" }
            }
            """;
        var g = SteamScanner.ParseManifest(@"D:\SteamLibrary", acf)!;
        Assert.Equal("steam.620", g.Id);
        Assert.Equal("Portal 2", g.Name);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Portal 2", g.InstallPath);
        Assert.Equal("mdi:steam", g.Icon);
    }

    [Fact]
    public void Exclusions()
    {
        var cfg = new SteamConfig { ExcludeAppIds = [10] };
        Assert.True(SteamScanner.IsExcluded(new GameEntity(228980, "Steamworks Common Redistributables", "x"), cfg));
        Assert.True(SteamScanner.IsExcluded(new GameEntity(10, "Counter-Strike", "x"), cfg));
        Assert.True(SteamScanner.IsExcluded(new GameEntity(11, "Proton Runtime", "x"), cfg));
        Assert.True(SteamScanner.IsExcluded(new GameEntity(12, "Some Game Soundtrack", "x"), cfg));
        Assert.False(SteamScanner.IsExcluded(new GameEntity(620, "Portal 2", "x"), cfg));
    }
}

public class StoreAndAuthTests
{
    [Fact]
    public void RevisionChangesOnlyWhenSetChanges()
    {
        var store = new EntityStore();
        long r0 = store.Revision;
        Assert.True(store.Replace([new GameEntity(1, "A", @"C:\a"), new GameEntity(2, "B", @"C:\b")]));
        long r1 = store.Revision;
        Assert.Equal(r0 + 1, r1);

        var a = store.Get("steam.1")!;
        a.State = EntityStates.On;
        Assert.False(store.Replace([new GameEntity(1, "A", @"C:\a"), new GameEntity(2, "B", @"C:\b")]));
        Assert.Equal(r1, store.Revision);
        Assert.Same(a, store.Get("steam.1"));
        Assert.Equal(EntityStates.On, store.Get("steam.1")!.State);

        Assert.True(store.Replace([new GameEntity(1, "A", @"C:\a")]));
        Assert.Equal(r1 + 1, store.Revision);
        Assert.Null(store.Get("steam.2"));
    }

    [Fact]
    public void BusyFlag()
    {
        var g = new GameEntity(1, "A", "x");
        Assert.True(g.TryBeginAction());
        Assert.False(g.TryBeginAction());
        Assert.True(g.Busy);
        g.EndAction();
        Assert.True(g.TryBeginAction());
    }

    [Theory]
    [InlineData("Bearer secret", true)]
    [InlineData("bearer secret", true)]
    [InlineData("Bearer secret2", false)]
    [InlineData("Bearer ", false)]
    [InlineData("secret", false)]
    [InlineData("", false)]
    public void Auth(string header, bool ok) => Assert.Equal(ok, ApiHost.IsAuthorized(header, "secret"));

    [Fact]
    public void TokenIsBase64Url32Bytes()
    {
        var t = ConfigStore.GenerateToken();
        Assert.Equal(43, t.Length);
        Assert.DoesNotContain('+', t);
        Assert.DoesNotContain('/', t);
        Assert.DoesNotContain('=', t);
    }
}
