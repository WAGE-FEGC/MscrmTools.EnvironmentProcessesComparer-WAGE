using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MscrmTools.EnvironmentProcessesComparer.AppCode;
using MscrmTools.EnvironmentProcessesComparer.Forms;
using MscrmTools.EnvironmentProcessesComparer.UserControls;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using ProcessInfo = MscrmTools.EnvironmentProcessesComparer.AppCode.ProcessInfo;

namespace MscrmTools.EnvironmentProcessesComparer
{
    public partial class MyPluginControl : MultipleConnectionsPluginControlBase, IPayPalPlugin, IGitHubPlugin
    {
        private List<ProcessInfo> _processInfos;

        private Thread filterThread;
        private List<Entity> solutions = new List<Entity>();

        // Shift-click range selection support
        private ListViewItem _lastCheckedItem = null;
        private bool _isBatchUpdating = false;

        // Debounce timer for "Selection in progress" label
        private System.Windows.Forms.Timer _selectionDebounceTimer;

        public MyPluginControl()
        {
            InitializeComponent();

            _selectionDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _selectionDebounceTimer.Tick += (s, e) =>
            {
                _selectionDebounceTimer.Stop();
                UpdateSelectionCount();
            };
        }

        public string DonationDescription => "Donation for Environment Processes Comparer";

        public string EmailAccount => "tanguy92@hotmail.com";

        public string RepositoryName => "MscrmTools";

        public string UserName => "MscrmTools.EnvironmentProcessesComparer";

        /// <summary>
        /// This event occurs when the connection has been updated in XrmToolBox
        /// </summary>
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);

            if (actionName != "AdditionalOrganization")
            {
                lvProcesses.Columns[2].Text = detail.ConnectionName;
                lvProcesses.Columns[2].Tag = detail;
                ClearContent();
            }
            else
            {
                tsbBulkUpdate.Visible = AdditionalConnectionDetails.Count > 0;
                tsbHideBulkUpdateLogs.Visible = AdditionalConnectionDetails.Count > 0;
                tssBulkUpdate.Visible = AdditionalConnectionDetails.Count > 0;
            }
        }

        protected override void ConnectionDetailsUpdated(NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (ConnectionDetail cd in e.NewItems)
                {
                    int index = lvProcesses.Columns.Count - 1;

                    lvProcesses.Columns.Add(new ColumnHeader { Text = cd.ConnectionName, Width = 100, Tag = cd });

                    LoadProcessesForAdditional(cd, index);
                }

                chkShowOnlyDifference.Enabled = true;
            }
        }

        private void ClearContent()
        {
            _lastCheckedItem = null;
            tsbSelectAll.Enabled = false;
            tsbDeselectAll.Enabled = false;
            tsbSelectEnabled.Enabled = false;
            lvProcesses.Items.Clear();
            AdditionalConnectionDetails.Clear();
            for (var i = lvProcesses.Columns.Count - 1; i >= 3; i--)
            {
                lvProcesses.Columns.RemoveAt(i);
            }

            scMain.Panel2Collapsed = true;
        }

        private void Ctrl_OnStateChangeRequested(object sender, StateChangeEventArgs e)
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Updating state...",
                Work = (bw, evt) =>
                {
                    UpdateProcessState(e.ConnectionDetail, e.record);
                },
                PostWorkCallBack = (evt) =>
                {
                    if (evt.Error != null)
                    {
                        MessageBox.Show(this, $@"Error while updating process state: {evt.Error.Message}", @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    ((ProcessStateControl)sender).SetInvertedState();

                    var item = lvProcesses.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ProcessInfo)i.Tag).Id == e.record.Id);
                    if (item == null) return;

                    item.SubItems[e.SubItemIndex].Text = e.State == 1 ? "True" : "False";

                    lvProcesses.RedrawItems(item.Index, item.Index, false);
                }
            });
        }

        private void DisplayProcesses(object term = null)
        {
            Thread.Sleep(100);

            if (_processInfos == null) return;

            Invoke(new Action(() =>
            {
                lvProcesses.SelectedIndexChanged -= lvProcesses_SelectedIndexChanged;
                lvProcesses.Items.Clear();
                lvProcesses.Items.AddRange(_processInfos.Where(p =>
                term == null || p.Item.Text.ToLower().IndexOf(term.ToString().ToLower()) >= 0)
                .Where(p => chkShowActions.Checked && p.CategoryCode == 3
                    || chkShowBusinessProcessFlows.Checked && p.CategoryCode == 4
                    || chkShowBusinessRules.Checked && p.CategoryCode == 2
                    || chkShowModernFlows.Checked && p.CategoryCode == 5
                    || chkShowWorkflows.Checked && p.CategoryCode == 0
                )
                .Where(p => !chkShowOnlyDifference.Checked || chkShowOnlyDifference.Checked && p.HasDifference)
                .Select(pi => pi.Item)
                .ToArray());

                foreach (ListViewItem item in lvProcesses.Items)
                {
                    var grp = ((ProcessInfo)item.Tag).Category;

                    if (lvProcesses.Groups[grp] == null)
                    {
                        lvProcesses.Groups.Add(grp, grp);
                    }

                    item.Group = lvProcesses.Groups[grp];
                }

                lvProcesses.SelectedIndexChanged += lvProcesses_SelectedIndexChanged;
            }));
        }

        private void filterCriteriaChanged(object sender, EventArgs e)
        {
            filterThread?.Abort();
            filterThread = new Thread(DisplayProcesses);
            filterThread.Start(tstbFilter.Text);
        }

        private void LoadProcesses(bool fromSolution = false)
        {
            if (fromSolution)
            {
                var dialog = new SolutionPicker(Service);

                if (dialog.ShowDialog(this) == DialogResult.Cancel) return;
                solutions = dialog.SelectedSolutions;
            }

            ClearContent();

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Fetching processes from Dataverse...",
                Work = (bw, e) =>
                {
                    var pManager = new ProcessManager(Service);
                    _processInfos = pManager.LoadProcesses(solutions).Select(p => new ProcessInfo(p, ConnectionDetail, 1)).ToList();
                },
                PostWorkCallBack = e =>
                {
                    if (e.Error != null)
                    {
                        MessageBox.Show(this, $@"Error while loading processes: {e.Error.Message}", @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var visibleItems = _processInfos.Where(p =>
                        string.IsNullOrEmpty(tstbFilter.Text) || p.Item.Text.ToLower().IndexOf(tstbFilter.Text.ToLower()) >= 0)
                    .Where(p => chkShowActions.Checked && p.CategoryCode == 3
                        || chkShowBusinessProcessFlows.Checked && p.CategoryCode == 4
                        || chkShowBusinessRules.Checked && p.CategoryCode == 2
                        || chkShowModernFlows.Checked && p.CategoryCode == 5
                        || chkShowWorkflows.Checked && p.CategoryCode == 0
                    )
                    .Where(p => !chkShowOnlyDifference.Checked || chkShowOnlyDifference.Checked && p.HasDifference)
                    .ToList();

                    // Show status in toolbar so user knows something is happening after overlay disappears
                    lblStatus.Text = "Loading - please wait!";
                    lblStatus.ForeColor = System.Drawing.Color.Red;
                    lblStatus.Font = new System.Drawing.Font(lblStatus.Font, System.Drawing.FontStyle.Bold);
                    lblStatus.Refresh();

                    lvProcesses.SelectedIndexChanged -= lvProcesses_SelectedIndexChanged;
                    lvProcesses.BeginUpdate();

                    foreach (var pi in visibleItems)
                    {
                        var grp = pi.Category;
                        if (lvProcesses.Groups[grp] == null)
                        {
                            lvProcesses.Groups.Add(grp, grp);
                        }
                        pi.Item.Group = lvProcesses.Groups[grp];
                    }

                    lvProcesses.Items.AddRange(visibleItems.Select(pi => pi.Item).ToArray());
                    lvProcesses.EndUpdate();

                    lvProcesses.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
                    lvProcesses.AutoResizeColumn(1, ColumnHeaderAutoResizeStyle.ColumnContent);
                    lvProcesses.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.HeaderSize);
                    lvProcesses.SelectedIndexChanged += lvProcesses_SelectedIndexChanged;

                    tsbAddFromOtherEnvs.Enabled = true;
                    tsbBulkUpdate.Enabled = true;
                    tsbHideBulkUpdateLogs.Enabled = true;
                    tsbSelectAll.Enabled = true;
                    tsbDeselectAll.Enabled = true;
                    tsbSelectEnabled.Enabled = true;
                    lblStatus.ForeColor = System.Drawing.Color.DimGray;
                    lblStatus.Font = new System.Drawing.Font(lblStatus.Font, System.Drawing.FontStyle.Italic);
                    UpdateSelectionCount();
                }
            });
        }

        private void LoadProcessesForAdditional(ConnectionDetail detail, int index)
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading processes...",
                Work = (bw, e) =>
                {
                    var pManager = new ProcessManager(detail.GetCrmServiceClient());
                    e.Result = pManager.LoadProcesses(solutions).Select(p => new ProcessInfo(p, detail, index)).ToList();
                },
                PostWorkCallBack = e =>
                {
                    if (e.Error != null)
                    {
                        MessageBox.Show(this, $@"Error while loading processes: {e.Error.Message}", @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var newItems = new List<ListViewItem>();

                    foreach (var pi in (List<ProcessInfo>)e.Result)
                    {
                        var existingPi = _processInfos.FirstOrDefault(p => p.Id.Equals(pi.Id));
                        if (existingPi != null)
                        {
                            if (existingPi.Item.SubItems.Count < index + 2)
                            {
                                existingPi.Item.SubItems.Add(new ListViewItem.ListViewSubItem());
                            }
                            existingPi.Item.SubItems[index + 1].Text = pi.IsEnabled.ToString();
                            existingPi.AddProcess(detail, pi);
                        }
                        else
                        {
                            var grp = pi.Category;

                            if (lvProcesses.Groups[grp] == null)
                            {
                                lvProcesses.Groups.Add(grp, grp);
                            }

                            pi.Item.Group = lvProcesses.Groups[grp];

                            newItems.Add(pi.Item);
                            _processInfos.Add(pi);
                        }
                    }

                    if (newItems.Count > 0)
                    {
                        lvProcesses.Items.AddRange(newItems.ToArray());
                    }

                    foreach (ListViewItem item in lvProcesses.Items.Cast<ListViewItem>().Where(i => i.SubItems.Count != lvProcesses.Columns.Count))
                    {
                        while (item.SubItems.Count != lvProcesses.Columns.Count)
                        {
                            item.SubItems.Add(new ListViewItem.ListViewSubItem());
                        }
                    }

                    lvProcesses.AutoResizeColumn(lvProcesses.Columns.Cast<ColumnHeader>().First(c => c.Tag == detail).Index, ColumnHeaderAutoResizeStyle.HeaderSize);
                    lvProcesses.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.HeaderSize);
                }
            });
        }

        private void lvProcesses_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            lvProcesses.Sorting = lvProcesses.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            lvProcesses.ListViewItemSorter = new ListViewItemComparer(e.Column, lvProcesses.Sorting);
        }

        private void lvProcesses_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void lvProcesses_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex < 2)
            {
                e.DrawDefault = true;
                return;
            }

            if (e.Item.SubItems.Count > 2)
            {
                if (e.Item.Selected)
                {
                    e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
                }

                var content = e.SubItem.Text ?? "";
                switch (content)
                {
                    case "True":
                        content = "Enabled";
                        break;

                    case "False":
                        content = "Disabled";
                        break;

                    default:
                        content = string.Empty;
                        break;
                }

                e.Graphics.FillRectangle(new SolidBrush(content.Length == 0 ? Color.FromArgb(248, 215, 218) : Color.FromArgb(209, 231, 221)), e.Bounds);

                var size = e.Graphics.MeasureString(content, e.Item.ListView.Font, e.Bounds.Size, StringFormat.GenericDefault, out int _, out int _);

                e.Graphics.DrawString(content, e.Item.ListView.Font, new SolidBrush(content == "Enabled" ? Color.FromArgb(15, 81, 50) : Color.FromArgb(132, 32, 41)), e.Bounds.X + (e.Bounds.Width - size.Width) / 2, e.Bounds.Y);
            }
            else
            {
                e.DrawDefault = true;
            }
        }

        private void lvProcesses_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Show "Selection in progress" while group selection is settling,
            // then debounce back to the normal count after 300ms of inactivity
            lblStatus.Text = "Selection in progress...";
            _selectionDebounceTimer.Stop();
            _selectionDebounceTimer.Start();

            var selectedItem = lvProcesses.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
            if (selectedItem == null) return;

            var record = ((ProcessInfo)selectedItem.Tag).Record;

            scMain.Panel2.Controls.Clear();

            var list = new List<Control>();

            list.Add(new System.Windows.Forms.Label
            {
                Text = record.GetAttributeValue<string>("name"),
                AutoSize = false,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(lvProcesses.Font, FontStyle.Bold)
            });

            for (int i = 2; i < selectedItem.SubItems.Count; i++)
            {
                var envName = lvProcesses.Columns[i].Text;
                var cd = AdditionalConnectionDetails.FirstOrDefault(c => c.ConnectionName == envName) ?? ConnectionDetail;

                if (!((ProcessInfo)selectedItem.Tag).Statuses.ContainsKey(cd)) continue;

                var suRecord = ((ProcessInfo)selectedItem.Tag).Statuses[cd];
                var ctrl = new ProcessStateControl(suRecord, cd, i) { Dock = DockStyle.Top, Height = 70 };
                ctrl.OnStateChangeRequested += Ctrl_OnStateChangeRequested;
                list.Add(ctrl);
            }

            list.Reverse();

            scMain.Panel2.Controls.AddRange(list.ToArray());
            scMain.Panel2Collapsed = false;
        }

        private void UpdateSelectionCount()
        {
            int checked_ = lvProcesses.CheckedItems.Count;
            int total = lvProcesses.Items.Count;
            lblStatus.Text = $"{checked_} of {total} selected";
        }

        private void lvProcesses_MouseClick(object sender, MouseEventArgs e)
        {
            // Only handle left-clicks — right-click is for the context menu
            if (e.Button != MouseButtons.Left) return;

            var clickedItem = lvProcesses.GetItemAt(e.X, e.Y);
            if (clickedItem == null) return;

            if (ModifierKeys == Keys.Shift && _lastCheckedItem != null)
            {
                int start = Math.Min(_lastCheckedItem.Index, clickedItem.Index);
                int end = Math.Max(_lastCheckedItem.Index, clickedItem.Index);
                // Checked has already toggled by the time MouseClick fires, so use current state
                bool targetState = clickedItem.Checked;

                _isBatchUpdating = true;
                for (int i = start; i <= end; i++)
                {
                    lvProcesses.Items[i].Checked = targetState;
                }
                _isBatchUpdating = false;
                UpdateSelectionCount();
            }
            else
            {
                _lastCheckedItem = clickedItem;
            }
        }

        private void lvProcesses_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_isBatchUpdating) return;
            UpdateSelectionCount();
        }

        private void lvProcesses_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _isBatchUpdating = true;
                foreach (ListViewItem item in lvProcesses.Items)
                {
                    item.Checked = true;
                }
                _isBatchUpdating = false;
                UpdateSelectionCount();
                e.Handled = true;
            }
        }

        private void tsbSelectEnabled_Click(object sender, EventArgs e)
        {
            // Column 2 is always the primary environment state column
            _isBatchUpdating = true;
            foreach (ListViewItem item in lvProcesses.Items)
            {
                item.Checked = item.SubItems.Count > 2 && item.SubItems[2].Text == "True";
            }
            _isBatchUpdating = false;
            UpdateSelectionCount();
        }

        private void tsbSelectAll_Click(object sender, EventArgs e)
        {
            _isBatchUpdating = true;
            foreach (ListViewItem item in lvProcesses.Items)
            {
                item.Checked = true;
            }
            _isBatchUpdating = false;
            UpdateSelectionCount();
        }

        private void tsbDeselectAll_Click(object sender, EventArgs e)
        {
            _isBatchUpdating = true;
            foreach (ListViewItem item in lvProcesses.Items)
            {
                item.Checked = false;
            }
            _isBatchUpdating = false;
            UpdateSelectionCount();
        }

        private void tsbAddFromOtherEnvs_Click(object sender, EventArgs e)
        {
            AddAdditionalOrganization();
        }

        private void tsbBulkUpdate_Click(object sender, EventArgs e)
        {

            using (var bulkForm = new BulkUpdateSettings(ConnectionDetail, AdditionalConnectionDetails.ToList()))
            {
                if (DialogResult.OK == bulkForm.ShowDialog(this))
                {
                    scSecondary.Panel2Collapsed = false;
                    lvBulkUpdateLogs.Items.Clear();

                    if (bulkForm.IsUpdateAcrossEnvs)
                    {
                        var sourceDetail = bulkForm.Source;
                        var targetDetails = bulkForm.Targets;
                        var onlyChecked = bulkForm.OnlyCheckedProcesses;

                        if (onlyChecked && lvProcesses.CheckedItems.Count == 0)
                        {
                            MessageBox.Show(this, "You did not checked any process", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        var targetIndexes = new List<int>();
                        int sourceIndex = -1;

                        foreach (var col in lvProcesses.Columns.Cast<ColumnHeader>().Where(c => c.Tag != null))
                        {
                            var cd = (ConnectionDetail)col.Tag;
                            if (cd == sourceDetail)
                            {
                                sourceIndex = col.Index;
                            }
                            else if (targetDetails.Contains(cd))
                            {
                                targetIndexes.Add(col.Index);
                            }
                        }

                        var processesToUpdate = onlyChecked
                                ? lvProcesses.Items.Cast<ListViewItem>().Where(p => p.Checked).Select(p => (ProcessInfo)p.Tag).ToList()
                                : lvProcesses.Items.Cast<ListViewItem>().Select(p => (ProcessInfo)p.Tag).ToList();

                        WorkAsync(new WorkAsyncInfo
                        {
                            Message = "Updating processes...",
                            Work = (bw, evt) =>
                            {
                                var pManager = new ProcessManager(sourceDetail.GetCrmServiceClient());

                                foreach (var process in processesToUpdate)
                                {
                                    foreach (var targetIndex in targetIndexes)
                                    {
                                        if (process.Item.SubItems[targetIndex].Text == process.Item.SubItems[sourceIndex].Text)
                                        {
                                            continue;
                                        }

                                        bw.ReportProgress(0, $"Processing {process.Name} on {lvProcesses.Columns[targetIndex].Text}...");

                                        var toUpdate = new Entity(process.Record.LogicalName)
                                        {
                                            Id = process.Record.Id
                                        };
                                        toUpdate["statecode"] = new OptionSetValue(process.Item.SubItems[sourceIndex].Text == "True" ? 1 : 0);
                                        toUpdate["statuscode"] = new OptionSetValue(process.Item.SubItems[sourceIndex].Text == "True" ? 2 : 1);
                                        try
                                        {
                                            var cd = ((ConnectionDetail)lvProcesses.Columns[targetIndex].Tag);
                                            cd.GetCrmServiceClient().Update(toUpdate);
                                            process.Statuses[cd]["statecode"] = toUpdate["statecode"];
                                            process.Statuses[cd]["statuscode"] = toUpdate["statuscode"];
                                            bw.ReportProgress(0, new BulkUpdateInfo
                                            {
                                                Success = true,
                                                ProcessName = process.Name,
                                                TargetEnvironment = lvProcesses.Columns[targetIndex].Text,
                                                Message = $"{((int)toUpdate["statecode"] == 0 ? "Disabled" : "Enabled")} successfully"
                                            });
                                        }
                                        catch (Exception error)
                                        {
                                            bw.ReportProgress(0, new BulkUpdateInfo
                                            {
                                                Success = false,
                                                ProcessName = process.Name,
                                                TargetEnvironment = lvProcesses.Columns[targetIndex].Text,
                                                Message = error.Message
                                            });
                                        }
                                    }
                                }

                                evt.Result = processesToUpdate;
                            },
                            ProgressChanged = evt =>
                            {
                                if (evt.UserState is BulkUpdateInfo bui)
                                {
                                    lvBulkUpdateLogs.Items.Add(new ListViewItem
                                    {
                                        Text = bui.ProcessName,
                                        SubItems =
                                            {
                                            new ListViewItem.ListViewSubItem
                                            {
                                                Text = bui.TargetEnvironment
                                            },
                                            new ListViewItem.ListViewSubItem
                                            {
                                                Text = bui.Message
                                            }
                                            },
                                        ForeColor = bui.Success ? Color.Green : Color.Red
                                    });

                                    scSecondary.Panel2Collapsed = false;

                                    return;
                                }

                                SetWorkingMessage(evt.UserState.ToString());
                            },
                            PostWorkCallBack = evt =>
                            {
                                var processes = (List<ProcessInfo>)evt.Result;
                                foreach (var process in processes)
                                {
                                    process.UpdateListViewItem();
                                }
                            }
                        });
                    }
                    else
                    {
                        if (lvProcesses.CheckedItems.Count == 0)
                        {
                            MessageBox.Show(this, "You did not checked any process", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        var processesToUpdate = lvProcesses.Items.Cast<ListViewItem>().Where(p => p.Checked).Select(p => (ProcessInfo)p.Tag).ToList();
                        var connections = bulkForm.Targets;
                        var isEnabled = bulkForm.SetToEnabled;

                        var targetIndexes = new List<int>();

                        foreach (var col in lvProcesses.Columns.Cast<ColumnHeader>().Where(c => c.Tag != null))
                        {
                            var cd = (ConnectionDetail)col.Tag;
                            if (connections.Contains(cd))
                            {
                                targetIndexes.Add(col.Index);
                            }
                        }

                        WorkAsync(new WorkAsyncInfo
                        {
                            Message = "Updating processes...",
                            Work = (bw, evt) =>
                            {
                                foreach (var process in processesToUpdate)
                                {
                                    foreach (var targetIndex in targetIndexes)
                                    {
                                        bw.ReportProgress(0, $"Processing {process.Name} on {lvProcesses.Columns[targetIndex].Text}...");

                                        var toUpdate = new Entity(process.Record.LogicalName)
                                        {
                                            Id = process.Record.Id
                                        };
                                        toUpdate["statecode"] = new OptionSetValue(isEnabled ? 1 : 0);
                                        toUpdate["statuscode"] = new OptionSetValue(isEnabled ? 2 : 1);

                                        try
                                        {
                                            var cd = ((ConnectionDetail)lvProcesses.Columns[targetIndex].Tag);
                                            cd.GetCrmServiceClient().Update(toUpdate);
                                            process.Statuses[cd]["statecode"] = toUpdate["statecode"];
                                            process.Statuses[cd]["statuscode"] = toUpdate["statuscode"];
                                            bw.ReportProgress(0, new BulkUpdateInfo
                                            {
                                                Success = true,
                                                ProcessName = process.Name,
                                                TargetEnvironment = lvProcesses.Columns[targetIndex].Text,
                                                Message = $"{(((OptionSetValue)toUpdate["statecode"]).Value == 0 ? "Disabled" : "Enabled")} successfully"
                                            });
                                        }
                                        catch (Exception error)
                                        {
                                            bw.ReportProgress(0, new BulkUpdateInfo
                                            {
                                                Success = false,
                                                ProcessName = process.Name,
                                                TargetEnvironment = lvProcesses.Columns[targetIndex].Text,
                                                Message = error.Message
                                            });
                                        }
                                    }
                                }

                                evt.Result = processesToUpdate;
                            },
                            ProgressChanged = evt =>
                            {
                                if (evt.UserState is BulkUpdateInfo bui)
                                {
                                    lvBulkUpdateLogs.Items.Add(new ListViewItem
                                    {
                                        Text = bui.ProcessName,
                                        SubItems =
                                            {
                                            new ListViewItem.ListViewSubItem
                                            {
                                                Text = bui.TargetEnvironment
                                            },
                                            new ListViewItem.ListViewSubItem
                                            {
                                                Text = bui.Message
                                            }
                                            },
                                        ForeColor = bui.Success ? Color.Green : Color.Red
                                    });

                                    scSecondary.Panel2Collapsed = false;

                                    return;
                                }

                                SetWorkingMessage(evt.UserState.ToString());
                            },
                            PostWorkCallBack = evt =>
                            {
                                var processes = (List<ProcessInfo>)evt.Result;
                                foreach (var process in processes)
                                {
                                    process.UpdateListViewItem();
                                }
                            }
                        });
                    }
                }
            }

        }

        private void tsbExportToExcel_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "CSV file (*.csv)|*.csv";
                sfd.Title = "Select the destination file";
                sfd.FileName = "Processes.csv";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    string filePath = sfd.FileName;

                    using (var writer = new System.IO.StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                    {
                        // Écrire l'en-tête
                        var headers = lvProcesses.Columns
                            .Cast<ColumnHeader>()
                            .Select(col => "\"" + col.Text.Replace("\"", "\"\"") + "\"");
                        writer.WriteLine(string.Join(",", headers));

                        // Écrire les lignes
                        foreach (ListViewItem item in lvProcesses.Items)
                        {
                            var values = item.SubItems
                                .Cast<ListViewItem.ListViewSubItem>()
                                .Select(sub => "\"" + (sub.Text == "" ? "" : sub.Text == "True" ? "Enabled" : sub.Text == "False" ? "Disabled" : sub.Text).Replace("\"", "\"\"") + "\"");
                            writer.WriteLine(string.Join(",", values));
                        }
                    }

                    if (DialogResult.Yes == MessageBox.Show(this, "Do you want to open generated file ? (Requires Microsoft Excel)", "Question", MessageBoxButtons.YesNo, MessageBoxIcon.Question))
                    {
                        Process.Start("Excel.exe", $"\"{filePath}\"");
                    }
                }
            }
        }

        private void tsbHideBulkUpdateLogs_Click(object sender, EventArgs e)
        {
            scSecondary.Panel2Collapsed = true;
        }

        private void tsddbLoad_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (e.ClickedItem == tsmiAllProcesses)
            {
                ExecuteMethod(LoadProcesses, false);
            }
            else
            {
                ExecuteMethod(LoadProcesses, true);
            }
        }

        private void UpdateProcessState(ConnectionDetail detail, Entity process)
        {
            var toUpdate = new Entity(process.LogicalName)
            {
                Id = process.Id
            };

            process["statecode"] = new OptionSetValue(process.GetAttributeValue<OptionSetValue>("statecode").Value == 1 ? 1 : 0);
            process["statuscode"] = new OptionSetValue(process.GetAttributeValue<OptionSetValue>("statecode").Value == 1 ? 2 : 1);
            toUpdate["statecode"] = process["statecode"];
            toUpdate["statuscode"] = process["statuscode"];
            detail.GetCrmServiceClient().Update(toUpdate);
        }
    }
}