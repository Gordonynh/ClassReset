using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClassIsland.PluginShared;

/// <summary>
/// 点选式的列表编辑器：已选的显示成一排小块，点一下移除；
/// 下面列出候选项，点一下添加。可选地再给一个手动输入框。
/// </summary>
/// <remarks>
/// 用来替换「逗号分隔的输入框」。那种框有两个绕不开的毛病：
/// 用户得自己记格式，而且输入过程中的规范化会跟打字互相干扰。
/// 点选没有格式问题，候选项直接来自课表 / 值日表 / 当前开着的程序，照着点就行。
/// </remarks>
public sealed class TokenListEditor : StackPanel
{
    private readonly WrapPanel _selected = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly WrapPanel _suggestions = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _empty = new()
    {
        FontSize = 12,
        Opacity = 0.55,
        Text = "未选择。"
    };

    private readonly TextBlock _suggestionLabel = new()
    {
        FontSize = 12,
        Opacity = 0.55,
        Margin = new Thickness(0, 6, 0, 0),
        Text = "点击添加："
    };

    private readonly TextBox _custom = new()
    {
        MinWidth = 120,
        FontSize = 13
    };

    private readonly TextBlock _customError = new()
    {
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F)),
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false
    };

    private Func<List<string>> _read = () => [];
    private Action<List<string>> _write = _ => { };
    private Func<IEnumerable<string>> _suggest = () => [];
    private Func<string, string?>? _normalize;

    public TokenListEditor()
    {
        Spacing = 4;
        Margin = new Thickness(42, 6, 42, 10);

        Children.Add(_selected);
        Children.Add(_suggestionLabel);
        Children.Add(_suggestions);

        var customRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var addButton = new Button { Content = "添加", FontSize = 13 };
        addButton.Click += (_, _) => CommitCustom();
        _custom.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitCustom();
                e.Handled = true;
            }
        };
        customRow.Children.Add(_custom);
        customRow.Children.Add(addButton);
        customRow.Children.Add(_customError);
        Children.Add(customRow);
        _customRow = customRow;
    }

    private readonly StackPanel _customRow;

    /// <summary>
    /// 接上数据。
    /// </summary>
    /// <param name="read">取当前列表。</param>
    /// <param name="write">保存新列表。</param>
    /// <param name="suggestions">候选项，每次重建时取一遍，已选的会自动滤掉。</param>
    /// <param name="customWatermark">手动输入框的提示。传 null 就不给手动输入。</param>
    /// <param name="normalize">手动输入的规范化，返回 null 表示格式不对。</param>
    /// <param name="invalidHint">格式不对时的提示。</param>
    public void Configure(Func<List<string>> read, Action<List<string>> write,
        Func<IEnumerable<string>> suggestions, string? customWatermark = null,
        Func<string, string?>? normalize = null, string invalidHint = "格式无法识别")
    {
        _read = read;
        _write = write;
        _suggest = suggestions;
        _normalize = normalize;
        _invalidHint = invalidHint;
        _customRow.IsVisible = customWatermark is not null;
        _custom.Watermark = customWatermark ?? string.Empty;
        Rebuild();
    }

    private string _invalidHint = "";

    /// <summary>外部改了列表之后让界面跟上。</summary>
    public void Rebuild()
    {
        var current = _read();

        _selected.Children.Clear();
        if (current.Count == 0)
        {
            _selected.Children.Add(_empty);
        }
        else
        {
            foreach (var value in current)
            {
                _selected.Children.Add(Chip($"{value}  ✕", accent: true, () => Remove(value)));
            }
        }

        var pool = _suggest()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !current.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _suggestions.Children.Clear();
        foreach (var value in pool)
        {
            _suggestions.Children.Add(Chip($"+ {value}", accent: false, () => Add(value)));
        }

        _suggestionLabel.IsVisible = pool.Count > 0;
        _suggestions.IsVisible = pool.Count > 0;
    }

    private static Button Chip(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 13,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(14)
        };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    private void Add(string value)
    {
        var current = _read();
        if (!current.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(value);
            _write(current);
        }

        Rebuild();
    }

    private void Remove(string value)
    {
        var current = _read();
        current.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        _write(current);
        Rebuild();
    }

    private void CommitCustom()
    {
        var raw = (_custom.Text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return;
        }

        var value = _normalize is null ? raw : _normalize(raw);
        if (value is null)
        {
            _customError.Text = _invalidHint;
            _customError.IsVisible = true;
            return;
        }

        _customError.IsVisible = false;
        _custom.Text = string.Empty;
        Add(value);
    }
}
