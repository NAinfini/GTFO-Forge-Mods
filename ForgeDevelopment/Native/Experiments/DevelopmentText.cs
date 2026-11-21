using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>
/// The package's one copy table: every word the experiment panel and the drive-session player protection show, in English and
/// Chinese, chosen by the game's text language. A caller never writes a literal; an unknown key is a programming error and throws.
/// </summary>
internal static class DevelopmentText
{
    private static readonly Dictionary<string, (string En, string Zh)> Table = new(StringComparer.Ordinal)
    {
        ["panel.title"] = ("<b>Forge Development experiments (F5)</b>", "<b>Forge 开发实验（F5）</b>"),
        ["panel.reload"] = ("reload files", "重读文件"),
        ["panel.close"] = ("close", "关闭"),
        ["panel.observe"] = ("Observe local player model (read only)", "观察本机玩家模型（只读）"),
        ["panel.commands"] = ("{0} commands from {1}", "{0} 条命令，来自 {1}"),
        ["panel.crosshair"] = ("crosshair: {0} | {1} | {2}", "准星：{0} | {1} | {2}"),
        ["panel.nearest"] = ("nearest: {0} | {1}", "最近：{0} | {1}"),
        ["panel.objects"] = ("experiment objects alive: {0}   [R] destroy them", "实验对象存活：{0}   [R] 销毁"),
        ["panel.running"] = ("running: {0}   [Esc] cancel", "运行中：{0}   [Esc] 取消"),
        ["panel.unassigned"] = ("unassigned", "未分配"),
        ["panel.run"] = ("run selected [Enter]", "运行所选 [Enter]"),
        ["panel.copy-id"] = ("copy id", "复制 ID"),
        ["panel.command-title"] = ("<b>{0}</b>", "<b>{0}</b>"),
        ["panel.source"] = ("source: {0}", "来源：{0}"),
        ["panel.target"] = ("target: {0}   -> {1}", "目标：{0}   -> {1}"),
        ["panel.points"] = ("points: {0}", "测试点：{0}"),
        ["panel.none"] = ("none", "无"),
        ["panel.requires"] = ("requires: {0}", "前置：{0}"),
        ["panel.cleanup"] = ("cleanup: {0} step(s)", "清理：{0} 步"),
        ["panel.last"] = ("last: {0} {1}/{2} at {3} ({4} ms)", "最近一次：{0} {1}/{2} 于 {3}（{4} 毫秒）"),
        ["panel.recent"] = ("<b>recent</b>", "<b>最近</b>"),
        ["panel.status.done"] = ("[done]", "[完成]"),
        ["panel.status.fail"] = ("[fail]", "[失败]"),
        ["panel.status.skip"] = ("[skip]", "[跳过]"),
        ["panel.status.stop"] = ("[stop]", "[停止]"),
        ["panel.status.pending"] = ("[    ]", "[    ]"),
        ["panel.target-unknown"] = ("unknown selector", "未知选择器"),
        ["step.call"] = ("call {0}({1})", "调用 {0}（{1}）"),
        ["step.set"] = ("set {0}", "写入 {0}"),
        ["step.read"] = ("read {0}", "读取 {0}"),
        ["step.read-screenshot"] = (" + screenshot", " + 截图"),
        ["step.wait"] = ("wait {0}", "等待 {0}"),
        ["step.screenshot"] = ("screenshot", "截图"),
        ["step.world-event"] = ("worldEvent", "世界事件"),
        ["step.repeat"] = ("repeat x{0} ({1} steps)", "重复 {0} 次（{1} 步）"),
        ["wait.frames"] = ("{0} frames", "{0} 帧"),
        ["wait.seconds"] = ("{0}s", "{0} 秒"),
        ["wait.trace"] = ("trace {0}.{1} within {2} frames", "轨迹 {0}.{1}，{2} 帧内"),
        ["wait.change"] = ("change of {0} within {1} frames", "{0} 变化，{1} 帧内"),
        ["wait.presence"] = ("presence of {0} within {1} frames", "{0} 出现，{1} 帧内"),
        ["wait.nothing"] = ("nothing", "无")
    };

    /// <summary>The text of <paramref name="key"/> in the game's text language.</summary>
    internal static string T(string key)
    {
        if (!Table.TryGetValue(key, out var row)) throw new KeyNotFoundException("development copy key: " + key);
        return Chinese ? row.Zh : row.En;
    }

    /// <summary>The text of <paramref name="key"/> with its placeholders filled, culture-invariant.</summary>
    internal static string T(string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, T(key), args);

    private static bool Chinese
    {
        get
        {
            try
            {
                return Localization.Text.TextLocalizationService?.CurrentLanguage
                    is Localization.Language.Chinese_Simplified or Localization.Language.Chinese_Traditional;
            }
            catch (Exception) { return false; } // The text service exists only after game data; English is the fallback then.
        }
    }
}
