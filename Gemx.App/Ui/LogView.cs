namespace Gemx.App;

// ---------------------------------------------------------------------------------------------
// Event log. RichTextBox backed so the text stays selectable and copyable, but the colours,
// severity rails and trimming are ours.
// ---------------------------------------------------------------------------------------------
internal sealed class LogView : UserControl
{
    readonly RichTextBox _rtb = new();
    const int TrimLines = 1200;

    public bool AutoScroll { get; set; } = true;
    public bool Frozen { get; set; }
    public int LineCount { get; private set; }

    public LogView()
    {
        BackColor = Pal.Card;
        _rtb.Dock = DockStyle.Fill;
        _rtb.BorderStyle = BorderStyle.None;
        _rtb.BackColor = Pal.Card;
        _rtb.ForeColor = Pal.Text;
        _rtb.Font = Fonts.Mono;
        _rtb.ReadOnly = true;
        _rtb.WordWrap = false;
        _rtb.DetectUrls = false;
        _rtb.HideSelection = false;
        _rtb.ScrollBars = RichTextBoxScrollBars.Vertical;
        _rtb.ShortcutsEnabled = true;
        _rtb.TabStop = false;
        Controls.Add(_rtb);
    }

    public void Append(string line)
    {
        if (Frozen) return;
        if (line.Length == 0) return;

        // "HH:mm:ss.fff " prefix is dimmed, the rest is coloured by severity
        string stamp = "";
        string body = line;
        if (line.Length >= 13 && line[12] == ' ' && line[2] == ':' && line[5] == ':')
        {
            stamp = line[..12];
            body = line[13..];
        }

        Color ink = Pal.Text;
        string upper = body.ToUpperInvariant();
        if (upper.Contains("ERROR") || upper.Contains("FAIL") || upper.Contains("REJECT") || upper.Contains("401") || upper.Contains("403") || upper.Contains("NOT CONFIRMED"))
            ink = Pal.DownLit;
        else if (upper.Contains("TRIP") || upper.Contains("BREAKER") || upper.Contains("KILL") || upper.Contains("WARN") || upper.Contains("OVERFLOW") || upper.Contains("GAP"))
            ink = Pal.Warn;
        else if (upper.Contains("FILL") || upper.Contains("STOPPED") || upper.Contains("ACK"))
            ink = Pal.UpLit;
        else if (upper.Contains("START") || upper.Contains("SUBSCRIBE") || upper.Contains("CONNECT"))
            ink = Pal.Info;
        else if (upper.Contains("MD:") || upper.Contains("ORDERS:"))
            ink = Pal.TextDim;

        _rtb.SelectionStart = _rtb.TextLength;
        _rtb.SelectionLength = 0;
        _rtb.SelectionColor = ink;
        _rtb.AppendText(line + "\r\n");
        _rtb.SelectionStart = Math.Max(0, _rtb.TextLength - 2);
        _rtb.SelectionLength = 0;
        _rtb.SelectionColor = ink;
        LineCount++;

        if (LineCount > 2400) Trim();
        if (AutoScroll) ScrollToEnd();
    }

    public void Clear()
    {
        _rtb.Clear();
        LineCount = 0;
    }

    public void CopyAll()
    {
        try
        {
            if (_rtb.TextLength > 0) Clipboard.SetText(_rtb.Text);
        }
        catch { }
    }

    public void ScrollToEnd()
    {
        try
        {
            _rtb.SelectionStart = _rtb.TextLength;
            _rtb.SelectionLength = 0;
            _rtb.ScrollToCaret();
        }
        catch { }
    }

    void Trim()
    {
        int cut = _rtb.GetFirstCharIndexFromLine(TrimLines);
        if (cut <= 0) return;
        _rtb.Select(0, cut);
        _rtb.SelectedText = "";
        _rtb.Select(_rtb.TextLength, 0);
        LineCount = Math.Max(0, LineCount - TrimLines);
    }
}
