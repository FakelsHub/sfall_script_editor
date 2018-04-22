﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.ComponentModel;

using ICSharpCode.TextEditor;
using ICSharpCode.TextEditor.Document;

using ScriptEditor.CodeTranslation;
using ScriptEditor.TextEditorUI;
using ScriptEditor.TextEditorUtilities;

using ScriptEditor.SyntaxRules;

namespace ScriptEditor
{
    partial class TextEditor
    {
        HighlightWord customHighlight;
        
        #region ParseFunction
        public event EventHandler ParserUpdatedInfo; // Event for update nodes diagram

        private bool firstParse;

        public void intParserPrint(string info)
        {
            if (!Settings.enableParser)
                tbOutputParse.Text = info + tbOutputParse.Text;
        }

        // Parse first open script
        private void FirstParseScript(TabInfo cTab)
        {
            customHighlight = new HighlightWord();

            tbOutputParse.Text = string.Empty;

            firstParse = true;
            
            GetMacros.GetGlobalMacros(Settings.pathHeadersFiles);
            
            DEBUGINFO("First Parse...");
            new Parser(cTab, this);
            
            while (parserRunning) 
                System.Threading.Thread.Sleep(10); //Avoid stomping on files while the parser is running
            
            var ExtParser = new ParserDLL(firstParse);
            cTab.parseInfo = ExtParser.Parse(cTab.textEditor.Text, cTab.filepath, cTab.parseInfo);
            DEBUGINFO("External first parse status: " + ExtParser.LastStatus);

            CodeFolder.UpdateFolding(cTab.textEditor.Document, cTab.filename, cTab.parseInfo.procs);
            CodeFolder.LoadFoldCollapse(cTab.textEditor.Document);

            GetParserErrorLog(cTab);

            if (cTab.parseInfo.parseError) {
                tabControl2.SelectedIndex = 2;
                maximize_log();
            }

            firstParse = false;

            customHighlight.ProceduresHighlight(cTab.textEditor.Document, cTab.parseInfo.procs);
        }

        // Parse script
        private void ParseScript(int delay = 2)
        {
            if (!Settings.enableParser) {
                int iDelay;
                if (delay > 1)
                    iDelay = delay / 2;
                else
                    iDelay = 0;
                intParser_TimeNext = DateTime.Now + TimeSpan.FromSeconds(iDelay);
                if (!intParserTimer.Enabled)
                    intParserTimer.Start();
            } else {
                intParser_TimeNext = DateTime.Now + TimeSpan.FromMilliseconds(500);
                if (!intParserTimer.Enabled)
                    intParserTimer.Start();
            }

            extParser_TimeNext = DateTime.Now + TimeSpan.FromSeconds(delay);
            if (!extParserTimer.Enabled)
                extParserTimer.Start(); // External Parser begin
        }
        
        //Force update parser data
        private void ForceParseScript()
        {
            // останавливаем ранее сработавшие таймеры
            intParserTimer.Stop();
            extParserTimer.Stop();

            if (Settings.enableParser && currentTab.parseInfo.parseData) {
                TextEditor.parserRunning = true; // parse work
                CodeFolder.UpdateFolding(currentDocument, currentTab.filepath);
                bwSyntaxParser.RunWorkerAsync(new WorkerArgs(currentDocument.TextContent, currentTab));
            } else {
                new Parser(currentTab, this);
                CodeFolder.UpdateFolding(currentTab.textEditor.Document, currentTab.filename, currentTab.parseInfo.procs);
                ParserCompleted(currentTab);
            }
        }

        // Delay timer for internal parsing
        void InternalParser_Tick(object sender, EventArgs e)
        {
            if (currentTab == null /*|| !currentTab.shouldParse*/) {
                intParserTimer.Stop();
                DEBUGINFO("Stop: Internal Parser");
                return;
            }

            if (DateTime.Now > intParser_TimeNext && !parserRunning) {
                DEBUGINFO("Run: Internal Parser");
                intParserTimer.Stop();
                if (!Settings.enableParser) { // Parser off
                    tbOutputParse.Text = string.Empty;
                    parserLabel.Text = "Parser: Get only macros";
                    parserLabel.ForeColor = Color.Crimson;

                    new Parser(currentTab, this);
                    CodeFolder.UpdateFolding(currentTab.textEditor.Document, currentTab.filename, currentTab.parseInfo.procs);
                    ParserCompleted(currentTab);
                } else {
                    CodeFolder.UpdateFolding(currentDocument, currentTab.filepath);
                    //Quick update procedure data
                    Parser.UpdateProcInfo(ref currentTab.parseInfo, currentDocument.TextContent, currentTab.filepath);
                }
            }
        }

        // Timer for parsing
        void ExternalParser_Tick(object sender, EventArgs e)
        {
            if (currentTab == null || !currentTab.shouldParse) {
                extParserTimer.Stop();
                DEBUGINFO("Stop: External Parser");
                return;
            }
            if (DateTime.Now > extParser_TimeNext && !bwSyntaxParser.IsBusy && !parserRunning) {
                if (autoComplete.IsVisible)
                    return;

                DEBUGINFO("Run: External Parser");
                parserRunning = true;
                if (Settings.enableParser) {
                    parserLabel.Text = "Parser: Working";
                    parserLabel.ForeColor = Color.Crimson;
                }
                bwSyntaxParser.RunWorkerAsync(new WorkerArgs(currentDocument.TextContent, currentTab));
                extParserTimer.Stop();
            }
        }

        // Parse Start
        private void bwSyntaxParser_DoWork(object sender, DoWorkEventArgs eventArgs)
        {
            WorkerArgs args = (WorkerArgs)eventArgs.Argument;
            var ExtParser = new ParserDLL(false);
            args.tab.parseInfo = ExtParser.Parse(args.text, args.tab.filepath, args.tab.parseInfo);
            args.status = ExtParser.LastStatus;
            eventArgs.Result = args;
            parserRunning = false;
        }

        // Parse Stop
        private void bwSyntaxParser_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!Settings.enableParser)
                return; // выход для предотвращения второго прохода когда внешний парсер выключен

            DEBUGINFO(">>> Ext parse status: " + e.Result.ToString());

            if (!(((WorkerArgs)e.Result).tab is TabInfo))
                throw new Exception("TabInfo is expected!");

            ParserCompleted(((WorkerArgs)e.Result).tab as TabInfo);
        }

        private void ParserCompleted(TabInfo tab)
        {
            if (ParserUpdatedInfo != null)
                    ParserUpdatedInfo(this, EventArgs.Empty); // Event for update

            GetParserErrorLog(tab);

            if (currentTab == tab) {
                if (tab.filepath != null) {
                    if (tab.parseInfo.parsed) {
                        if (tab.textEditor.Document.FoldingManager.FoldMarker.Count > 0) //tab.parseInfo.procs.Length
                            Outline_toolStripButton.Enabled = true;
                        
                        customHighlight.ProceduresHighlight(tab.textEditor.Document, tab.parseInfo.procs);

                        UpdateNames(); // Update Tree Variables/Procedures
                        
                        if (Settings.enableParser)
                            parserLabel.Text = (!tab.parseInfo.parseError) ? "Parser: Complete" : "Parser: Script syntax error (see parser errors log)";
                        else
                            parserLabel.Text = parseoff + " [Get only macros]";
                        tab.needsParse = false;
                    } else {
                        parserLabel.Text = (Settings.enableParser) ? "Parser: Failed script parsing (see parser errors log)" : parseoff + " [Get only macros]";
                        //currentTab.needsParse = true; // требуется обновление
                    }
                } else {
                    parserLabel.Text = (Settings.enableParser) ? "Parser: Get only local macros" : parseoff;
                }
            }
        }

        private void GetParserErrorLog(TabInfo tab)
        {
            string log = String.Empty;
            if (File.Exists("errors.txt")) {
                try { 
                    log = File.ReadAllText("errors.txt");
                    File.Delete("errors.txt");    
                } catch (IOException) {
                    //в случаях ошибки в parser.dll, не освобождается созданный им файл, что приводит к ошибке доступа
                    File.Copy("errors.txt", "parser.log");
                    log = File.ReadAllText("parser.log");
                    File.Delete("parser.log");
                }
            }
            tab.parserLog = Error.ParserLog(log, tab);
            OutputErrorLog(tab);
        }

        private void OutputErrorLog(TabInfo tab)
        {
            dgvErrors.Rows.Clear();
            if (Settings.enableParser && tsmShowParserLog.Checked) {
                tbOutputParse.Text = tab.parserLog;
                foreach (Error err in tab.parserErrors)
                    dgvErrors.Rows.Add(err.type.ToString(), Path.GetFileName(err.fileName), err.line, err);
            }
            if (tab.buildLog != null && tsmShowBuildLog.Checked) {
                tbOutput.Text = tab.buildLog;
                dgvErrors.Rows.Add("Build Log");
                dgvErrors.Rows[dgvErrors.Rows.Count - 1].DefaultCellStyle.BackColor = Color.Gainsboro;
                foreach (Error err in tab.buildErrors)
                    dgvErrors.Rows.Add(err.type.ToString(), Path.GetFileName(err.fileName), err.line, err);
            }
        }

        private void textChanged(object sender, EventArgs e)
        {
            if (!currentTab.changed) {
                currentTab.changed = true;
                SetTabTextChange(currentTab.index);
            }
            if (sender != null && currentTab.shouldParse) {
                if (currentTab.shouldParse && !currentTab.needsParse) {
                    currentTab.needsParse = true;
                    parserLabel.Text = "Parser: Update change";
                }
                // Update parse info
                ParseScript(4);
            }
        }
        #endregion

        #region Search Function
        private bool SubSearchInternal(List<int> offsets, List<int> lengths)
        {
            RegexOptions option = RegexOptions.None;
            Regex regex = null;

            if (!sf.cbCase.Checked)
                option = RegexOptions.IgnoreCase;

            if (sf.cbRegular.Checked)
                regex = new Regex(sf.tbSearch.Text, option);
            else if (sf.cbWord.Checked)
                regex = new Regex(@"\b" + sf.tbSearch.Text + @"\b", option);

            if (sf.rbFolder.Checked && Settings.lastSearchPath == null) {
                MessageBox.Show("No search path set.", "Error");
                return false;
            }
            if (!sf.cbFindAll.Checked) {
                if (sf.rbCurrent.Checked || (sf.rbAll.Checked && tabs.Count < 2)) {
                    if (currentTab == null)
                        return false;
                    if (Utilities.SearchAndScroll(currentActiveTextAreaCtrl, regex, sf.tbSearch.Text, sf.cbCase.Checked, ref PosChangeType))
                        return true;
                } else if (sf.rbAll.Checked) {
                    int starttab = currentTab == null ? 0 : currentTab.index;
                    int endtab = starttab == 0 ? tabs.Count - 1 : starttab - 1;
                    int tab = starttab - 1;
                    int caretOffset = currentActiveTextAreaCtrl.Caret.Offset;
                    do {
                        if (++tab == tabs.Count)
                            tab = 0; //restart tab
                        int start, len;
                        if (Utilities.Search(tabs[tab].textEditor.Text, sf.tbSearch.Text, regex, caretOffset + 1, false, sf.cbCase.Checked, out start, out len)) {
                            Utilities.FindSelected(tabs[tab].textEditor.ActiveTextAreaControl, start, len, ref PosChangeType);
                            if (currentTab == null || currentTab.index != tab)
                                tabControl1.SelectTab(tab);
                            return true;
                        }
                        caretOffset = 0; // search from begin 
                    } while (tab != endtab);
                } else {
                    sf.lbFindFiles.Items.Clear();
                    sf.lbFindFiles.Tag = regex;
                    List<string> files = sf.GetFolderFiles();
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (Utilities.Search(File.ReadAllText(files[i]), sf.tbSearch.Text, regex, sf.cbCase.Checked))
                            sf.lbFindFiles.Items.Add(files[i]);
                    }
                    sf.labelCount.Text = sf.lbFindFiles.Items.Count.ToString();
                    if (sf.lbFindFiles.Items.Count > 0) {
                        sf.Height = 468;
                        return true;
                    }
                }
            } else {
                DataGridView dgv = CommonDGV.DataGridCreate();
                dgv.DoubleClick += dgvErrors_DoubleClick;

                if (sf.rbCurrent.Checked || (sf.rbAll.Checked && tabs.Count < 2)) {
                    if (currentTab == null)
                        return false;
                    Utilities.SearchForAll(currentTab, sf.tbSearch.Text, regex, sf.cbCase.Checked, dgv, offsets, lengths);
                } else if (sf.rbAll.Checked) {
                    for (int i = 0; i < tabs.Count; i++)
                        Utilities.SearchForAll(tabs[i], sf.tbSearch.Text, regex, sf.cbCase.Checked, dgv, offsets, lengths);
                } else {
                    List<string> files = sf.GetFolderFiles();
                    for (int i = 0; i < files.Count; i++)
                        Utilities.SearchForAll(File.ReadAllLines(files[i]), Path.GetFullPath(files[i]), sf.tbSearch.Text, regex, sf.cbCase.Checked, dgv);
                }
                if (dgv.RowCount > 0) {
                    TabPage tp = new TabPage("Search results");
                    tp.ToolTipText = "Find text: " + sf.tbSearch.Text;
                    tp.Controls.Add(dgv);
                    dgv.Dock = DockStyle.Fill;
                    tabControl2.TabPages.Add(tp);
                    tabControl2.SelectTab(tp);
                    maximize_log();
                    return true;
                }
            }
            MessageBox.Show("Search string not found", "Search");
            return false;
        }
        #endregion

        #region Search&Replace function form
        private void findToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (sf == null) {
                sf = new SearchForm();
                sf.Owner = this;
                sf.FormClosing += delegate(object a1, FormClosingEventArgs a2) { sf = null; };
                sf.KeyUp += delegate(object a1, KeyEventArgs a2) {
                    if (a2.KeyCode == Keys.Escape) {
                        sf.Close();
                    }
                };
                sf.rbFolder.CheckedChanged += delegate(object a1, EventArgs a2) {
                    sf.bChange.Enabled = sf.cbSearchSubfolders.Enabled = sf.rbFolder.Checked;
                    sf.bReplace.Enabled = !sf.rbFolder.Checked;
                };
                sf.tbSearch.KeyPress += delegate(object a1, KeyPressEventArgs a2) { if (a2.KeyChar == '\r') { bSearch_Click(null, null); a2.Handled = true; } };
                sf.bChange.Click += delegate(object a1, EventArgs a2) {
                    if (sf.fbdSearchFolder.ShowDialog() != DialogResult.OK)
                        return;
                    Settings.lastSearchPath = sf.fbdSearchFolder.SelectedPath;
                    sf.textBox1.Text = Settings.lastSearchPath;
                };
                sf.lbFindFiles.MouseDoubleClick += delegate (object a1, MouseEventArgs a2) {
                    string file = sf.lbFindFiles.SelectedItem.ToString();
                    Utilities.SearchAndScroll(Open(file, OpenType.File).textEditor.ActiveTextAreaControl,
                                             (Regex)sf.lbFindFiles.Tag, sf.tbSearch.Text, sf.cbCase.Checked, ref PosChangeType);
                };
                sf.bSearch.Click += new EventHandler(bSearch_Click);
                sf.bReplace.Click += new EventHandler(bReplace_Click);
                sf.Show();
            } else {
                sf.WindowState = FormWindowState.Normal;
                sf.Focus();
                sf.tbSearch.Focus();
            }
            string str = "";
            if (currentTab != null) {
                str = currentActiveTextAreaCtrl.SelectionManager.SelectedText;
            }
            if (str.Length == 0 || str.Length > 255) {
                str = Clipboard.GetText();
            }
            if (str.Length > 0 && str.Length < 255) {
                sf.tbSearch.Text = str;
                sf.tbSearch.SelectAll();
            }
        }

        private void bSearch_Click(object sender, EventArgs e)
        {
            sf.tbSearch.Text = sf.tbSearch.Text.Trim();
            if (sf.tbSearch.Text.Length == 0)
                return;
            SubSearchInternal(null, null);
        }

        void bReplace_Click(object sender, EventArgs e)
        {
            sf.tbSearch.Text = sf.tbSearch.Text.Trim();
            if (sf.rbFolder.Checked || sf.tbSearch.Text.Length == 0)
                return;
            if (sf.cbFindAll.Checked) {
                List<int> lengths = new List<int>(), offsets = new List<int>();
                if (!SubSearchInternal(offsets, lengths))
                    return;
                for (int i = offsets.Count - 1; i >= 0; i--)
                {
                    currentDocument.Replace(offsets[i], lengths[i], sf.tbReplace.Text);
                }
            } else {
                currentActiveTextAreaCtrl.Caret.Column--;
                if (!SubSearchInternal(null, null))
                    return;
                ISelection selected = currentActiveTextAreaCtrl.SelectionManager.SelectionCollection[0];
                currentDocument.Replace(selected.Offset, selected.Length, sf.tbReplace.Text);
                selected.EndPosition = new TextLocation(selected.StartPosition.Column + sf.tbReplace.Text.Length, selected.EndPosition.Line);
                currentActiveTextAreaCtrl.SelectionManager.SetSelection(selected);
            }
        }

        // Search for quick panel
        private void FindForwardButton_Click(object sender, EventArgs e)
        {
            string find = SearchTextComboBox.Text.Trim();
            if (find.Length == 0 || currentTab == null)
                return;
            int z = Utilities.SearchPanel(currentTab.textEditor.Text, find, currentActiveTextAreaCtrl.Caret.Offset + 1,
                                            CaseButton.Checked, WholeWordButton.Checked);
            if (z != -1) 
                Utilities.FindSelected(currentActiveTextAreaCtrl, z, find.Length, ref PosChangeType);
            else 
                DontFind.Play();
            addSearchTextComboBox(find);
        }

        private void FindBackButton_Click(object sender, EventArgs e)
        {
            string find = SearchTextComboBox.Text.Trim();
            if (find.Length == 0 || currentTab == null)
                return;
            int offset = currentActiveTextAreaCtrl.Caret.Offset;
            string text = currentTab.textEditor.Text.Remove(offset);
            int z = Utilities.SearchPanel(text, find, offset - 1, CaseButton.Checked, WholeWordButton.Checked, true);
            if (z != -1)
                Utilities.FindSelected(currentActiveTextAreaCtrl, z, find.Length, ref PosChangeType);
            else 
                DontFind.Play();
            addSearchTextComboBox(find);
        }

        private void ReplaceButton_Click(object sender, EventArgs e)
        {
            string find = SearchTextComboBox.Text.Trim();
            if (find.Length == 0)
                return;
            string replace = ReplaceTextBox.Text.Trim();
            int z = Utilities.SearchPanel(currentTab.textEditor.Text, find, currentActiveTextAreaCtrl.Caret.Offset, 
                                            CaseButton.Checked, WholeWordButton.Checked);
            if (z != -1) 
                Utilities.FindSelected(currentActiveTextAreaCtrl, z, find.Length, ref PosChangeType, replace);
            else 
                DontFind.Play();
            addSearchTextComboBox(find);
        }

        private void ReplaceAllButton_Click(object sender, EventArgs e)
        {
            string find = SearchTextComboBox.Text.Trim();
            if (find.Length == 0)
                return;

            string replace = ReplaceTextBox.Text.Trim();
            int z, offset = 0;
            do {
                z = Utilities.SearchPanel(currentTab.textEditor.Text, find, offset, 
                                            CaseButton.Checked, WholeWordButton.Checked);
                if (z != -1) 
                    currentActiveTextAreaCtrl.Document.Replace(z, find.Length, replace);
                offset = z + 1;
            } while (z != -1);
            addSearchTextComboBox(find);
        }

        private void SendtoolStripButton_Click(object sender, EventArgs e)
        {
            string word = currentActiveTextAreaCtrl.SelectionManager.SelectedText;
            if (word == string.Empty) 
                word = TextUtilities.GetWordAt(currentDocument, currentActiveTextAreaCtrl.Caret.Offset);
            if (word != string.Empty) 
                SearchTextComboBox.Text = word;
        }

        private void quickFindToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (currentTab == null)
                return;

            SendtoolStripButton.PerformClick();
            FindForwardButton.PerformClick();
            if (!SearchToolStrip.Visible) {
                SearchToolStrip.Visible = true;
                TabClose_button.Top += (SearchToolStrip.Visible) ? 25 : -25;
            }
        }

        private void Search_Panel(object sender, EventArgs e)
        {
            if (currentTab == null && !SearchToolStrip.Visible) {
                findToolStripMenuItem_Click(null, null);
                return;
            }
            SearchToolStrip.Visible = !SearchToolStrip.Visible;
            TabClose_button.Top += (SearchToolStrip.Visible) ? 25 : -25;
        }
        #endregion

        #region References/DeclerationDefinition & Include function
        private void findReferencesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TextLocation tl = currentActiveTextAreaCtrl.Caret.Position; //(TextLocation)editorMenuStrip.Tag;
            string word = TextUtilities.GetWordAt(currentDocument, currentDocument.PositionToOffset(tl));

            Reference[] refs = currentTab.parseInfo.LookupReferences(word, currentTab.filepath, tl.Line);
            if (refs == null)
                return;
            if (refs.Length == 0) {
                MessageBox.Show("No references found", "Reference", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridView dgv = CommonDGV.DataGridCreate();
            dgv.DoubleClick += dgvErrors_DoubleClick;

            foreach (var r in refs)
            {
                Error error = new Error(ErrorType.Search) {
                    fileName = r.file,
                    line = r.line,
                    column = TextUtilities.GetLineAsString(currentDocument, r.line - 1).IndexOf(word, StringComparison.OrdinalIgnoreCase),
                    len = word.Length,
                    message = (String.Compare(Path.GetFileName(r.file), currentTab.filename, true) == 0) 
                               ? TextUtilities.GetLineAsString(currentDocument, r.line - 1).TrimStart() 
                               : "< Preview is not possible: for viewing goto this the reference link >"
                };
                if (error.column > 0)
                    error.column++;
                dgv.Rows.Add(r.file, r.line, error);
            }

            TabPage tp = new TabPage("'" + word + "' references");
            tp.Controls.Add(dgv);
            dgv.Dock = DockStyle.Fill;
            tabControl2.TabPages.Add(tp);
            tabControl2.SelectTab(tp);
            maximize_log();
            TextArea_SetFocus(null, null);
        }

        private void findDeclerationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TextLocation tl = currentActiveTextAreaCtrl.Caret.Position; //(TextLocation)editorMenuStrip.Tag;
            string word = TextUtilities.GetWordAt(currentDocument, currentDocument.PositionToOffset(tl));
            string file;
            int line;
            currentTab.parseInfo.LookupDecleration(word, currentTab.filepath, tl.Line, out file, out line);
            SelectLine(file, line);
        }

        private void findDefinitionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string word, file = currentTab.filepath;
            int line;
            TextLocation tl = currentActiveTextAreaCtrl.Caret.Position;
            if (((ToolStripDropDownItem)sender).Tag != null) { // "Button"
                if (!currentTab.shouldParse)
                    return;

                Parser.UpdateParseSSL(currentTab.textEditor.Text);
                
                word = TextUtilities.GetWordAt(currentDocument, currentDocument.PositionToOffset(tl));
                line = Parser.GetProcBeginEndBlock(word, 0, true).begin;
                if (line != -1)
                    line++; 
                else 
                    return;
            } else {
                //TextLocation tl = (TextLocation)editorMenuStrip.Tag;
                word = TextUtilities.GetWordAt(currentDocument, currentDocument.PositionToOffset(tl));
                currentTab.parseInfo.LookupDefinition(word, out file, out line);
            }
            SelectLine(file, line);
        }

        private void openIncludeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TextLocation tl = currentActiveTextAreaCtrl.Caret.Position; //(TextLocation)editorMenuStrip.Tag;
            string[] line = TextUtilities.GetLineAsString(currentDocument, tl.Line).Split('"');
            if (line.Length < 2)
                return;

            if (!Settings.overrideIncludesPath && currentTab.filepath == null && !Path.IsPathRooted(line[1])) {
                MessageBox.Show("Cannot open includes given via a relative path for an unsaved script", "Error");
                return;
            }
            
            Parser.OverrideIncludePath(ref line[1], Path.GetDirectoryName(currentTab.filepath));
            if (Open(line[1], OpenType.File, false) == null)
                MessageBox.Show("Header file not found!", null, MessageBoxButtons.OK, MessageBoxIcon.Stop);
        }

        private void renameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Refactor.Rename((IParserInfo)renameToolStripMenuItem.Tag, currentDocument, currentTab, tabs);
        }
        #endregion
        
        #region Autocomplete and tips function control
        private void TextArea_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            //PosChangeType = PositionType.OverridePos; // Save position change for navigation, if key was pressed
            
            if (autoComplete.IsVisible) {
                autoComplete.TA_PreviewKeyDown(e);
                if (Settings.autocomplete && e.KeyCode == Keys.Back) {
                    autoComplete.GenerateList(String.Empty, currentTab, 
                        currentActiveTextAreaCtrl.Caret.Offset - 1, toolTips.Tag, true);
                }
            }
            if (toolTips.Active) {
                if (e.KeyCode == Keys.Up || (e.KeyCode == Keys.Down && !autoComplete.IsVisible) 
                    || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape
                    || (toolTips.Tag != null && !(bool)toolTips.Tag)) {
                        ToolTipsHide();
                }
                else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) {
                    int caret = currentActiveTextAreaCtrl.Caret.Offset;
                    int offset = caret;
                    if (e.KeyCode == Keys.Left)
                        caret--;
                    else {
                        caret++;
                        offset = TextUtilities.SearchBracketForward(currentDocument, showTipsColumn + 1, '(', ')');
                    }
                    if (showTipsColumn >= caret || caret > offset) ToolTipsHide();
                }
            }
            if (e.KeyCode == Keys.Tab) { // Закрытие списка, если нажата клавиша таб после ключевого слова
                if (Utilities.AutoCompleteKeyWord(currentActiveTextAreaCtrl)) {
                    e.IsInputKey = true;
                    autoComplete.ShiftCaret = false;
                    if (autoComplete.IsVisible)
                        autoComplete.Close();
                }
            }
        }

        private void TextArea_MouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta == 0)
                return;
            
            autoComplete.TA_MouseScroll(currentTab.textEditor.ActiveTextAreaControl, e);

            if (toolTips.Active) ToolTipsHide();
        }

        private void ToolTipsHide()
        {
            if (autoComplete.IsVisible && (bool)toolTips.Tag)
                autoComplete.Close();
            
            toolTips.Hide(panel1);
            toolTips.Tag = toolTips.Active = false;
        }
        
        private void TextEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey)
                autoComplete.Hide();
        }

        private void TextEditor_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey)
                autoComplete.UnHide();
        }
        
        private void autocompleteCallToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Settings.autocomplete) { 
                Caret caret = currentActiveTextAreaCtrl.Caret;
                if (!ColorTheme.CheckColorPosition(currentDocument, caret.Position))
                    autoComplete.GenerateList(String.Empty, currentTab, caret.Offset, null);
            }
        }
        #endregion

        #region Navigation Back/Forward
        /*
         * AddPos       - Добавлять в историю новую позицию перемещения.
         * NoStore      - Не сохранять следующее перемещение в историю.
         * OverridePos  - Перезаписать позицию перемещения в текущей позиции истории.
         * Disabled     - Не сохранять все последуюшие перемещения в историю (до явного включения функции).
         */
        internal enum PositionType { AddPos, NoStore, OverridePos, Disabled }

        private void SetBackForwardButtonState() 
        {
            if (currentTab.history.pointerCur > 0)
                Back_toolStripButton.Enabled = true;
            else
                Back_toolStripButton.Enabled = false;
            
            if (currentTab.history.pointerCur == currentTab.history.pointerEnd || currentTab.history.pointerCur < 0)
                Forward_toolStripButton.Enabled = false;
            else if (currentTab.history.pointerCur > 0 || currentTab.history.pointerCur < currentTab.history.pointerEnd)
                Forward_toolStripButton.Enabled = true;
        }

        private void Caret_PositionChanged(object sender, EventArgs e)
        {
            string ext = Path.GetExtension(currentTab.filename).ToLower();
            if (ext != ".ssl" && ext != ".h")
                return;

            TextLocation _position = currentActiveTextAreaCtrl.Caret.Position;
            int curLine = _position.Line + 1;
            LineStripStatusLabel.Text = "Line: " + curLine;
            ColStripStatusLabel.Text = "Col: " + (_position.Column + 1);
            
            if (PosChangeType == PositionType.Disabled)
                return;
        PosChange:
            if (PosChangeType >= PositionType.NoStore) { // also OverridePos
                if (PosChangeType == PositionType.OverridePos && currentTab.history.pointerCur != -1)
                    currentTab.history.linePosition[currentTab.history.pointerCur] = _position;
                
                PosChangeType = PositionType.AddPos; // set default
                return;
            }

            int diff = Math.Abs(curLine - currentTab.history.prevPosition);
            currentTab.history.prevPosition = curLine;
            if (diff > 1) {
                currentTab.history.pointerCur++;
                if (currentTab.history.pointerCur >= currentTab.history.linePosition.Count)
                    currentTab.history.linePosition.Add(_position);
                else
                    currentTab.history.linePosition[currentTab.history.pointerCur] = _position;
                currentTab.history.pointerEnd = currentTab.history.pointerCur;
            } else {
                PosChangeType = PositionType.OverridePos;
                goto PosChange;
            }

            SetBackForwardButtonState();  
        }

        private void Back_toolStripButton_Click(object sender, EventArgs e)
        {
            if (currentTab == null || currentTab.history.pointerCur == 0)
                return;
            
            currentTab.history.pointerCur--;
            GotoViewLine(); 
        }

        private void Forward_toolStripButton_Click(object sender, EventArgs e)
        {
            if (currentTab == null || currentTab.history.pointerCur >= currentTab.history.pointerEnd)
                return;
            
            currentTab.history.pointerCur++;
            GotoViewLine();
        }

        private void GotoViewLine()
        {
            PosChangeType = PositionType.NoStore;
            TextLocation _position = currentTab.history.linePosition[currentTab.history.pointerCur];
            currentActiveTextAreaCtrl.Caret.Position = _position;
            currentTab.history.prevPosition = _position.Line + 1;

            int firstLine = currentActiveTextAreaCtrl.TextArea.TextView.FirstVisibleLine;
            int lastLine = firstLine + currentActiveTextAreaCtrl.TextArea.TextView.VisibleLineCount - 1;
            if (_position.Line <= firstLine || _position.Line + 1 >= lastLine)
                currentActiveTextAreaCtrl.CenterViewOn(currentActiveTextAreaCtrl.Caret.Line, 0);
            
            SetBackForwardButtonState();
        }
        #endregion

        #region Procedure function Create/Rename/Delete/Move 
        // Create Handlers Procedures
        public void CreateProcBlock(string name)
        {
            if (currentTab.parseInfo.CheckExistsName(name, false)) {
                MessageBox.Show("A procedure with this name has already been declared.", "Info");
                return;
            }

            byte inc = 0;
            if (name == "look_at_p_proc" || name == "description_p_proc")
                inc++;
            
            ProcForm CreateProcFrm = new ProcForm(name, true);
            if (ProcTree.SelectedNode != null && ProcTree.SelectedNode.Tag is Procedure)
                CreateProcFrm.checkBox1.Enabled = false;
            else 
                CreateProcFrm.groupBox1.Enabled = false;
            
            ProcTree.HideSelection = false;

            if (CreateProcFrm.ShowDialog() == DialogResult.Cancel) {
                ProcTree.HideSelection = true;
                return;
            }

            ProcBlock block = new ProcBlock();
            if (CreateProcFrm.radioButton2.Checked) {
                var proc = (Procedure)ProcTree.SelectedNode.Tag;
                block.begin = proc.d.start;
                block.end = proc.d.end;
                block.declar = proc.d.declared;  
            }

            PrepareInsertProcedure(CreateProcFrm.ProcedureName, block, CreateProcFrm.radioButton2.Checked, inc);
            
            CreateProcFrm.Dispose();
            ProcTree.HideSelection = true;
        }

        // Create Procedures
        private void createProcedureToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string word = null;
            bool IsSelectProcedure = ProcTree.SelectedNode != null && ProcTree.SelectedNode.Tag is Procedure;
            if (IsSelectProcedure)
                word = ProcTree.SelectedNode.Name;
            else if (currentActiveTextAreaCtrl.SelectionManager.HasSomethingSelected)
                word = currentActiveTextAreaCtrl.SelectionManager.SelectedText;
            ProcForm CreateProcFrm = new ProcForm(word, false, true);

            if (!IsSelectProcedure)
                CreateProcFrm.groupBox1.Enabled = false;
            
            ProcTree.HideSelection = false;
            if (CreateProcFrm.ShowDialog() == DialogResult.Cancel) {
                ProcTree.HideSelection = true;
                return;
            }

            string name = CreateProcFrm.CheckName;
            if (currentTab.parseInfo.CheckExistsName(name, NameType.Proc)) {
                MessageBox.Show("A procedure with this name has already been declared.", "Info");
                return;
            }
            
            ProcBlock block = new ProcBlock();
            if (CreateProcFrm.checkBox1.Checked || CreateProcFrm.radioButton2.Checked) {
                var proc = (Procedure)ProcTree.SelectedNode.Tag;
                block.begin = proc.d.start;
                block.end = proc.d.end;
                block.declar = proc.d.declared;
                block.copy = CreateProcFrm.checkBox1.Checked;
            }

            name = CreateProcFrm.ProcedureName;
            PrepareInsertProcedure(name, block, CreateProcFrm.radioButton2.Checked);
            
            CreateProcFrm.Dispose();
            ProcTree.HideSelection = true;
        }

        // Create procedure block
        private void PrepareInsertProcedure(string name, ProcBlock block, bool after = false, byte overrides = 0)
        {
            int procLine, declrLine, caretline = 3;
            string procbody;
            
            //Copy from procedure
            if (block.copy) {
                procbody = Utilities.GetRegionText(currentDocument, block.begin, block.end - 2) + Environment.NewLine;
                overrides = 1;
            } else 
                procbody = new string(' ', Settings.tabSize) + ("script_overrides;\r\n\r\n");
            
            string procblock = (overrides > 0)
                       ? "\r\nprocedure " + name + " begin\r\n" + procbody + "end"
                       : "\r\nprocedure " + name + " begin\r\n\r\nend";
            
            // declaration line
            if (after)
                declrLine = block.declar;
            else {
                Parser.UpdateParseSSL(currentTab.textEditor.Text);
                declrLine = Parser.GetEndLineProcDeclaration();
            }
            if (declrLine == -1) {
                declrLine = 0;
                MessageBox.Show("The declaration procedure is broken, declaration written to beginning of script.", "Warning");
            }
            // procedure line
            int total = currentDocument.TotalNumberOfLines - 1;
            if (after) {
                procLine = block.end; // after current procedure
                if (procLine > total)
                    procLine = block.end = total;
                else
                    block.end++;
                if (TextUtilities.GetLineAsString(currentDocument, block.end).Trim().Length > 0)
                    procblock += Environment.NewLine;
            } else
                procLine = total; // paste to end script
            
            Utilities.InsertProcedure(currentActiveTextAreaCtrl, name, procblock, declrLine, procLine, ref caretline);
            
            caretline += procLine + overrides;
            currentActiveTextAreaCtrl.Caret.Column = 0;
            currentActiveTextAreaCtrl.Caret.Line = caretline;
            currentActiveTextAreaCtrl.CenterViewOn(caretline, 0);
            
            ForceParseScript();
            SetFocusDocument();
        }

        // Rename Procedures
        private void renameProcedureToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Procedure proc = ProcTree.SelectedNode.Tag as Procedure;
            if (proc == null)
                return;
            
            ProcTree.HideSelection = false;
            string newName = Refactor.RenameProcedure(proc, currentDocument, currentTab, tabs);
            ProcTree.HideSelection = true;
            
            if (newName != null) {
                ForceParseScript();
                SetFocusDocument();
            }
        }

        // Delete Procedures
        private void deleteProcedureToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Procedure proc = ProcTree.SelectedNode.Tag as Procedure;
            if (proc == null)
                return;

            //if (proc.IsImported) {
            //    MessageBox.Show("You can't delete the imported procedure.");
            //    return;
            //}

            if (MessageBox.Show("Are you sure you want to delete \"" + proc.name + "\" procedure?",
                "Warning", MessageBoxButtons.YesNo) == DialogResult.No)
                return;

            Utilities.PrepareDeleteProcedure(proc, currentDocument);
            currentActiveTextAreaCtrl.SelectionManager.ClearSelection();

            ForceParseScript();
            SetFocusDocument();
        }

        private void MoveProcedure(int sIndex)
        {
            bool moveToEnd = false;
            int root = ProcTree.Nodes.Count - 1;

            if (sIndex > moveActive) {
                if (sIndex >= (ProcTree.Nodes[root].Nodes.Count - 1))
                    moveToEnd = true;
                else
                    sIndex++;
            } else if (sIndex == moveActive)
                return; //exit move

            Procedure moveProc = (Procedure)ProcTree.Nodes[root].Nodes[moveActive].Tag;
            // copy body
            Parser.UpdateParseSSL(currentDocument.TextContent);
            ProcBlock block = Parser.GetProcBeginEndBlock(moveProc.name, 0, true);
            block.declar = moveProc.d.declared;
            
            string copy_defproc;
            string copy_procbody = Environment.NewLine + Utilities.GetRegionText(currentDocument, block.begin, block.end);

            currentDocument.UndoStack.StartUndoGroup();
            currentActiveTextAreaCtrl.SelectionManager.ClearSelection();
            
            Utilities.DeleteProcedure(currentDocument, block, out copy_defproc);

            string name = ProcTree.Nodes[root].Nodes[sIndex].Text;
            
            Parser.UpdateParseSSL(currentDocument.TextContent);
            // insert declration
            int offset;
            if (copy_defproc != null) {
                int p_def = Parser.GetDeclarationProcedureLine(name);
                if (moveToEnd)
                    p_def++;
                offset = currentDocument.PositionToOffset(new TextLocation(0, p_def));
                currentDocument.Insert(offset, copy_defproc + Environment.NewLine);
            }
            //paste proc block
            block = Parser.GetProcBeginEndBlock(name, 0, true);
            int p_begin;
            if (moveToEnd) {
                p_begin = block.end + 1;
                copy_procbody = Environment.NewLine + copy_procbody;
            } else {
                p_begin = block.begin;
                copy_procbody += Environment.NewLine;
            }
            offset = currentDocument.PositionToOffset(new TextLocation(0, p_begin));
            offset += TextUtilities.GetLineAsString(currentDocument, p_begin).Length;
            
            currentDocument.Insert(offset, copy_procbody);
            currentDocument.UndoStack.EndUndoGroup();
            
            // Перемещение процедуры в дереве
            if (sIndex > moveActive && !moveToEnd)
                sIndex--;

            TreeNode nd = ProcTree.Nodes[root].Nodes[moveActive];
            ProcTree.Nodes[root].Nodes.RemoveAt(moveActive);
            ProcTree.Nodes[root].Nodes.Insert(sIndex, nd);
            ProcTree.SelectedNode = ProcTree.Nodes[root].Nodes[sIndex];
            ProcTree.Focus();
            ProcTree.Select();

            Parser.UpdateProcInfo(ref currentTab.parseInfo, currentDocument.TextContent, currentTab.filepath);
            CodeFolder.UpdateFolding(currentDocument, currentTab.filename, currentTab.parseInfo.procs);
        }

        private void moveProcedureToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ProcTree.SelectedNode == null)
                return;
            if (moveActive == -1) {
                moveActive = ProcTree.SelectedNode.Index;
                ProcTree.SelectedNode.ForeColor = Color.Red;
                ProcTree.AfterSelect -= TreeView_AfterSelect;
                ProcTree.SelectedNode = ProcTree.Nodes[0];
                ProcTree.AfterSelect += ProcTree_AfterSelect;
                //ProcTree.ShowNodeToolTips = false;
            }
        }

        private void ProcTree_MouseMove(object sender, MouseEventArgs e)
        {
            if (moveActive < 0)
                return;

            TreeNode node = ProcTree.GetNodeAt(e.Location);
            if (node != null && Functions.NodeHitCheck(e.Location, node.Bounds)) {
                if (node.Index > moveActive)
                    ProcTree.Cursor = Cursors.PanSouth;
                else
                    ProcTree.Cursor = Cursors.PanNorth;
            } else
                ProcTree.Cursor = Cursors.No;
        }

        private void ProcTree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Parent == null || e.Node.Parent.Text != TREEPROCEDURES[1])
                return;
            ProcTree.AfterSelect -= ProcTree_AfterSelect;
            currentTab.textEditor.TextChanged -= textChanged;
            MoveProcedure(e.Node.Index);
            currentTab.textEditor.TextChanged += textChanged;
            ProcTree.AfterSelect += TreeView_AfterSelect;
            ProcTree.SelectedNode.ForeColor = ProcTree.ForeColor;
            ProcTree.Cursor = Cursors.Hand;
            moveActive = -1;
            //ProcTree.ShowNodeToolTips = true;
            // set changed document
            textChanged(null, EventArgs.Empty);
        }

        private void ProcTree_MouseLeave(object sender, EventArgs e)
        {
            if (moveActive != -1) {
                ProcTree.AfterSelect -= ProcTree_AfterSelect;
                ProcTree.AfterSelect += TreeView_AfterSelect;
                ProcTree.Nodes[ProcTree.Nodes.Count - 1].Nodes[moveActive].ForeColor = ProcTree.ForeColor;
                ProcTree.Cursor = Cursors.Hand;
                moveActive = -1;
            }
        }

        private void ProcTree_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) {
                ProcTree_MouseLeave(null, null);
            }
        }

        private void ProcMnContext_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ProcTree.SelectedNode != null && ProcTree.SelectedNode.Parent != null 
                && (int)ProcTree.SelectedNode.Parent.Tag == 1 /*&& ProcTree.SelectedNode.Tag is Procedure*/) {
                Procedure proc = ProcTree.SelectedNode.Tag as Procedure;
                string pName = proc.name.ToLower();
                if (pName.IndexOf("node") > -1 || pName == "talk_p_proc")
                    editNodeCodeToolStripMenuItem.Enabled = true;
                else
                    editNodeCodeToolStripMenuItem.Enabled = false;
                renameProcedureToolStripMenuItem.Enabled = true;
                moveProcedureToolStripMenuItem.Enabled = true;
                deleteProcedureToolStripMenuItem.Enabled = true;
                deleteProcedureToolStripMenuItem.Text = "Delete: " + proc.name;
            } else {
                editNodeCodeToolStripMenuItem.Enabled = false;
                renameProcedureToolStripMenuItem.Enabled = false;
                moveProcedureToolStripMenuItem.Enabled = false;
                deleteProcedureToolStripMenuItem.Enabled = false;
                deleteProcedureToolStripMenuItem.Text = "Delete procedure";
            }
        }
        #endregion
    }
}
