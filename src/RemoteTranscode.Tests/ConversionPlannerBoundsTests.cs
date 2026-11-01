using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

/// <summary>Malformed or unexpected Emby conversion commands stay with Emby's ffmpeg instead of being guessed at.</summary>
public class ConversionPlannerBoundsTests
{
    private static readonly string[] Roots = ["/volume4/Film"];
    private const string Output = "/var/packages/EmbyServer/var/sync/1/2/x.mp4";

    private static string[] Valid() =>
        ["-y", "-i", "/volume4/Film/a.mkv", "-map", "0:0", "-map", "0:1", "-c:v:0", "libx264", "-crf:v:0", "22",
         "-c:a:0", "aac", "-map_metadata", "-1", "-f", "mp4", Output];

    [Fact]
    public void A_valid_command_is_still_offloaded()
    {
        var plan = EmbyConversionPlanner.Plan(Valid(), Roots);
        Assert.True(plan.Remote, plan.Reason);
    }

    [Fact]
    public void Input_without_path_is_refused_without_throwing()
    {
        Assert.False(EmbyConversionPlanner.Plan(["-y", "-i"], Roots).Remote);
        Assert.False(EmbyConversionPlanner.Plan(["-i"], Roots).Remote);
        Assert.False(EmbyConversionPlanner.Plan(["-y", "-i", Output], Roots).Remote);
    }

    [Fact]
    public void Map_without_value_is_refused_without_throwing()
    {
        string[] args = ["-i", "/volume4/Film/a.mkv", "-c:v:0", "libx264", "-map", Output];
        Assert.False(EmbyConversionPlanner.Plan(args, Roots).Remote);
    }

    [Fact]
    public void Filter_complex_without_value_is_refused_without_throwing()
    {
        string[] args = ["-i", "/volume4/Film/a.mkv", "-map", "0:0", "-c:v:0", "libx264", "-filter_complex", Output];
        Assert.False(EmbyConversionPlanner.Plan(args, Roots).Remote);
    }

    [Fact]
    public void Unknown_value_less_input_flag_does_not_carry_a_server_only_option_to_the_worker()
    {
        // "-some_new_flag" takes no value: guessing it does would copy -print_graphs_file into the worker command.
        string[] args = ["-some_new_flag", "-print_graphs_file", "/var/log/graph.txt", .. Valid()];
        var plan = EmbyConversionPlanner.Plan(args, Roots);
        Assert.False(plan.Remote);
        Assert.Contains("-some_new_flag", plan.Reason);
    }

    [Fact]
    public void Unknown_value_less_output_flag_is_refused()
    {
        string[] args = Valid();
        args = [.. args[..^1], "-some_new_flag", Output];
        Assert.False(EmbyConversionPlanner.Plan(args, Roots).Remote);
    }

    [Fact]
    public void Stray_value_is_refused()
    {
        string[] args = ["stray", .. Valid()];
        Assert.False(EmbyConversionPlanner.Plan(args, Roots).Remote);
    }
}
