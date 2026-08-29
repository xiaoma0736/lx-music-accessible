using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace LXMusicAccessible.App;

/// <summary>方向键交给内部控件或窗体处理，避免 ScrollViewer 预测并移动焦点。</summary>
public class SafeScrollViewer : ScrollViewer
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right) return;
        base.OnKeyDown(e);
    }
}

/// <summary>对自动化树隐藏的冗余视觉按钮。</summary>
public class InvisibleToScreenReaderButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new HiddenButtonAutomationPeer(this);

    private sealed class HiddenButtonAutomationPeer(Button owner) : ButtonAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}

/// <summary>对自动化树隐藏的冗余视觉文本。</summary>
public class InvisibleToScreenReaderTextBlock : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new HiddenTextBlockAutomationPeer(this);

    private sealed class HiddenTextBlockAutomationPeer(TextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}

/// <summary>把复合列表项作为单一叶节点暴露给屏幕阅读器。</summary>
public class SingleReadListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SingleReadListBoxAutomationPeer(this);
}

internal sealed class SingleReadListBoxAutomationPeer(ListBox owner) : ListBoxAutomationPeer(owner)
{
    protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
        new SingleReadListBoxItemAutomationPeer(item, this);
}

internal sealed class SingleReadListBoxItemAutomationPeer(
    object item,
    ListBoxAutomationPeer listPeer) : ListBoxItemAutomationPeer(item, listPeer)
{
    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrWhiteSpace(name) ? Item?.ToString() ?? string.Empty : name;
    }

    protected override List<AutomationPeer> GetChildrenCore() => [];
}
