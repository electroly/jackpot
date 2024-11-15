﻿using J.Core.Data;

namespace J.App;

public sealed class EditMoviesChooseTagForm : Form
{
    private readonly LibraryProviderAdapter _libraryProvider;
    private readonly TableLayoutPanel _table;
    private readonly ListBox _listBox;
    private readonly FlowLayoutPanel _buttonFlow;
    private readonly Button _okButton,
        _cancelButton;
    private List<Tag> _tags = [];

    public TagId? SelectedTag { get; private set; }

    public EditMoviesChooseTagForm(LibraryProviderAdapter libraryProvider)
    {
        _libraryProvider = libraryProvider;
        Ui ui = new(this);

        Controls.Add(_table = ui.NewTable(1, 2));
        {
            _table.Padding = ui.DefaultPadding;
            _table.RowStyles[0].SizeType = SizeType.Percent;
            _table.RowStyles[0].Height = 100;

            _table.Controls.Add(_listBox = ui.NewListBox(), 0, 0);
            {
                _listBox.DoubleClick += ListBox_DoubleClick;
            }

            _table.Controls.Add(_buttonFlow = ui.NewFlowRow(), 0, 1);
            {
                _buttonFlow.Dock = DockStyle.Right;
                _buttonFlow.Margin = ui.TopSpacingBig;

                _buttonFlow.Controls.Add(_okButton = ui.NewButton("OK"));
                {
                    _okButton.Click += OkButton_Click;
                }

                _buttonFlow.Controls.Add(_cancelButton = ui.NewButton("Cancel", DialogResult.Cancel));
            }
        }

        StartPosition = FormStartPosition.CenterParent;
        Size = ui.GetSize(300, 500);
        MinimumSize = ui.GetSize(300, 200);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        ShowIcon = false;
        ShowInTaskbar = false;
    }

    public void Initialize(TagType type)
    {
        _tags = [.. _libraryProvider.GetTags(type.Id).OrderBy(x => x.Name)];
        foreach (var tag in _tags)
            _listBox.Items.Add(tag.Name);
    }

    private void ListBox_DoubleClick(object? sender, EventArgs e)
    {
        if (_listBox.SelectedIndex < 0)
            return;

        Ok();
    }

    private void OkButton_Click(object? sender, EventArgs e)
    {
        if (_listBox.SelectedIndex < 0)
        {
            MessageBox.Show("Please select a tag.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Ok();
    }

    private void Ok()
    {
        SelectedTag = _tags[_listBox.SelectedIndex].Id;
        DialogResult = DialogResult.OK;
        Close();
    }
}
