using System;
using System.Drawing;
using System.Windows.Forms;

namespace BatteryTray
{

/// <summary>
/// 右键菜单的深色主题（参考 G HUB / WorkBuddy 的扁平观感）：
/// 统一由这套 ProfessionalRenderer 配色表驱动——背景、悬停、边框、分隔线、勾选符号
/// 全走深色，与悬停提示窗（TipWindow）同一套颜色。
/// 设计要点（用户：更扁平、更优雅、样式统一）：
///   · 悬停高亮只做"轻微提亮"的纯色块（无渐变、无圆角），扁平克制；
///   · 分隔线压到接近底色，几乎只是一道若隐若现的缝隙；
///   · 选中态符号（开机自启 ✓、设备固定点）统一落在最左勾选列，用浅色画，深底上清晰。
/// 配色写死深色（用户：先写死深色模式）。
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    // 菜单整体底色 = 悬停窗同款深底
    public override Color ToolStripDropDownBackground => BatteryUI.FormBg;
    public override Color ImageMarginGradientBegin => BatteryUI.FormBg;
    public override Color ImageMarginGradientMiddle => BatteryUI.FormBg;
    public override Color ImageMarginGradientEnd => BatteryUI.FormBg;

    // 外框：1px 浅边，干净利落（扁平不靠粗描边）
    public override Color MenuBorder => BatteryUI.BorderColor;
    // 选中项不再单独描边（扁平）：悬停/按下只靠底色提亮
    public override Color MenuItemBorder => Color.Transparent;

    // 悬停/按下：只做"轻微提亮"的纯色块（无渐变），扁平克制
    public override Color MenuItemSelected => Color.FromArgb(40, 40, 49);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(40, 40, 49);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(40, 40, 49);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(46, 46, 56);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(46, 46, 56);
    public override Color MenuItemPressedGradientMiddle => Color.FromArgb(46, 46, 56);

    // 分隔线压到接近底色，若隐若现的一道缝
    public override Color SeparatorDark => Color.FromArgb(36, 36, 44);
    public override Color SeparatorLight => Color.FromArgb(36, 36, 44);

    // 勾选底透明；开机自启的 ✓ 不再依赖系统默认勾（深底上颜色不可控），
    // 改由 ToggleMenuItem 在勾选列自绘浅色对勾（见 TrayApp.cs），与设备固定点同一列。
    public override Color CheckBackground => Color.Transparent;
    public override Color CheckSelectedBackground => Color.Transparent;
    public override Color CheckPressedBackground => Color.Transparent;
}

}
