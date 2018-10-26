﻿using Jamiras.Commands;
using Jamiras.Components;
using Jamiras.DataModels;
using Jamiras.Services;
using Jamiras.ViewModels;
using RATools.Parser.Internal;
using RATools.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RATools.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel()
        {
            NewScriptCommand = new DelegateCommand(NewScript);
            OpenScriptCommand = new DelegateCommand(OpenFile);
            SaveScriptCommand = DisabledCommand.Instance;
            SaveScriptAsCommand = DisabledCommand.Instance;
            RefreshScriptCommand = DisabledCommand.Instance;
            OpenRecentCommand = new DelegateCommand<string>(OpenFile);
            ExitCommand = new DelegateCommand(Exit);

            UpdateLocalCommand = DisabledCommand.Instance;

            GameStatsCommand = new DelegateCommand(GameStats);
            OpenTicketsCommand = new DelegateCommand(OpenTickets);

            AboutCommand = new DelegateCommand(About);

            _recentFiles = new RecencyBuffer<string>(8);
        }

        public bool Initialize()
        {
            var settings = new Settings();
            ServiceRepository.Instance.RegisterInstance<ISettings>(settings);
            ShowHexValues = settings.HexValues;

            var persistance = ServiceRepository.Instance.FindService<IPersistantDataRepository>();
            var recent = persistance.GetValue("RecentFiles");
            if (recent != null)
            {
                var list = new List<string>(recent.Split(';'));
                for (int i = list.Count - 1; i >= 0; i--) // add in reverse order so most recent is added last
                    _recentFiles.Add(list[i]);
                RecentFiles = list.ToArray();
            }

            var logService = ServiceRepository.Instance.FindService<ILogService>();
            FileLogger logger = null;
            for (int i = 1; i < 10; i++)
            {
                var fileName = (i == 1) ? "RATools.log" : "RATools" + i + ".log";
                try
                {
                    logger = new FileLogger(fileName);
                }
                catch (IOException)
                {
                    // assume "file in use" and create a new one
                    continue;
                }

                logService.Loggers.Add(logger);
                break;
            }
            logService.IsTimestampLogged = true;

            if (logger != null)
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                if (version.EndsWith(".0"))
                    version = version.Substring(0, version.Length - 2);
                logger.Write("RATools v" + version);
            }

            return true;
        }

        private RecencyBuffer<string> _recentFiles;

        public CommandBase ExitCommand { get; private set; }

        private void Exit()
        {
            ServiceRepository.Instance.FindService<IDialogService>().MainWindow.Close();
        }

        public CommandBase OpenScriptCommand { get; private set; }
        private void OpenFile()
        {
            var vm = new FileDialogViewModel();
            vm.DialogTitle = "Select achievements script";
            vm.Filters["Script file"] = "*.rascript;*.txt";
            vm.CheckFileExists = true;

            if (vm.ShowOpenFileDialog() == DialogResult.Ok)
                OpenFile(vm.FileNames[0]);
        }

        public CommandBase SaveScriptCommand { get; private set; }
        public CommandBase SaveScriptAsCommand { get; private set; }

        public static readonly ModelProperty GameProperty = ModelProperty.Register(typeof(MainWindowViewModel), "Game", typeof(GameViewModel), null, OnGameChanged);
        public GameViewModel Game
        {
            get { return (GameViewModel)GetValue(GameProperty); }
            private set { SetValue(GameProperty, value); }
        }

        private static void OnGameChanged(object sender, ModelPropertyChangedEventArgs e)
        {
            if (e.OldValue == null)
            {
                var vm = (MainWindowViewModel)sender;
                vm.SaveScriptCommand = new DelegateCommand(() => vm.SaveScript());
                vm.SaveScriptAsCommand = new DelegateCommand(() => vm.SaveScriptAs());
                vm.RefreshScriptCommand = new DelegateCommand(vm.RefreshScript);
                vm.UpdateLocalCommand = new DelegateCommand(vm.UpdateLocal);
                vm.OnPropertyChanged(() => vm.SaveScriptCommand);
                vm.OnPropertyChanged(() => vm.SaveScriptAsCommand);
                vm.OnPropertyChanged(() => vm.RefreshScriptCommand);
                vm.OnPropertyChanged(() => vm.UpdateLocalCommand);
            }
        }

        public static readonly ModelProperty RecentFilesProperty = ModelProperty.Register(typeof(MainWindowViewModel), "RecentFiles", typeof(IEnumerable<string>), null);
        public IEnumerable<string> RecentFiles
        {
            get { return (IEnumerable<string>)GetValue(RecentFilesProperty); }
            private set { SetValue(RecentFilesProperty, value); }
        }

        public CommandBase RefreshScriptCommand { get; private set; }
        private void RefreshScript()
        {
            if (Game != null)
            {
                Game.Script.DeleteBackup();
                OpenFile(Game.Script.Filename);
            }
        }

        public bool CloseEditor()
        {
            if (Game == null || Game.Script.CompareState != GeneratedCompareState.LocalDiffers)
                return true;

            var vm = new MessageBoxViewModel("Save changes to " + Game.Script.Title + "?");
            switch (vm.ShowYesNoCancelDialog())
            {
                case DialogResult.Yes:
                    return SaveScript();

                case DialogResult.No:
                    Game.Script.DeleteBackup();
                    return true;

                default:
                case DialogResult.Cancel:
                    return false;
            }
        }

        public CommandBase<string> OpenRecentCommand { get; private set; }
        private void OpenFile(string filename)
        {
            if (!File.Exists(filename))
            {
                MessageBoxViewModel.ShowMessage("Could not open " + filename);
                return;
            }

            int line = 1;
            int column = 1;
            string selectedEditor = null;
            if (Game != null && Game.Script.Filename == filename)
            {
                if (Game.Script.CompareState == GeneratedCompareState.LocalDiffers)
                {
                    var vm = new MessageBoxViewModel("Revert to the last saved state? Your changes will be lost.");
                    vm.DialogTitle = "Revert Script";
                    if (vm.ShowOkCancelDialog() == DialogResult.Cancel)
                        return;
                }

                // capture current location so we can restore it after refreshing
                line = Game.Script.Editor.CursorLine;
                column = Game.Script.Editor.CursorColumn;
                selectedEditor = Game.SelectedEditor.Title;
            }
            else if (!CloseEditor())
            {
                return;
            }

            var backupFilename = ScriptViewModel.GetBackupFilename(filename);
            bool usingBackup = false;
            if (File.Exists(backupFilename))
            {
                var vm2 = new MessageBoxViewModel("Found an autosave file from " + File.GetLastWriteTime(backupFilename) + ".\nDo you want to open it instead?");
                vm2.DialogTitle = Path.GetFileName(filename);
                switch (vm2.ShowYesNoCancelDialog())
                {
                    case DialogResult.Cancel:
                        return;

                    case DialogResult.Yes:
                        filename = backupFilename;
                        usingBackup = true;
                        break;

                    default:
                        break;
                }
            }

            var logger = ServiceRepository.Instance.FindService<ILogService>().GetLogger("RATools");
            logger.WriteVerbose("Opening " + filename);

            string content;
            try
            {
                content = File.ReadAllText(filename);
            }
            catch (IOException ex)
            {
                MessageBoxViewModel.ShowMessage(ex.Message);
                return;
            }

            var tokenizer = Tokenizer.CreateTokenizer(content);
            var expressionGroup = new AchievementScriptParser().Parse(tokenizer);

            int gameId = 0;
            var idComment = expressionGroup.Comments.FirstOrDefault(c => c.Value.Contains("#ID"));
            if (idComment != null)
            {
                var tokens = idComment.Value.Split('=');
                if (tokens.Length > 1)
                    Int32.TryParse(tokens[1].ToString(), out gameId);
            }

            if (gameId == 0)
            {
                logger.WriteVerbose("Could not find game ID");
                MessageBoxViewModel.ShowMessage("Could not find game id");
                return;
            }

            if (!usingBackup)
                AddRecentFile(filename);

            logger.WriteVerbose("Game ID: " + gameId);

            var gameTitle = expressionGroup.Comments[0].Value.Substring(2).Trim();
            GameViewModel viewModel = null;

            foreach (var directory in ServiceRepository.Instance.FindService<ISettings>().DataDirectories)
            {
                var notesFile = Path.Combine(directory, gameId + "-Notes2.txt");
                if (File.Exists(notesFile))
                {
                    logger.WriteVerbose("Found code notes in " + directory);

                    viewModel = new GameViewModel(gameId, gameTitle, directory.ToString());
                }
                else
                {
                    notesFile = Path.Combine(directory, gameId + "-Notes.json");
                    if (File.Exists(notesFile))
                    {
                        logger.WriteVerbose("Found code notes in " + directory);

                        viewModel = new GameViewModel(gameId, gameTitle, directory.ToString());
                    }
                }
            }

            if (viewModel == null)
            {
                logger.WriteVerbose("Could not find code notes");
                MessageBoxViewModel.ShowMessage("Could not locate notes file for game " + gameId);

                viewModel = new GameViewModel(gameId, gameTitle);
            }

            var existingViewModel = Game as GameViewModel;

            // if we're just refreshing the current game script, only update the script content,
            // which will be reprocessed and update the editor list. If it's not the same script,
            // or notes have changed, use the new view model.
            if (existingViewModel != null && existingViewModel.GameId == viewModel.GameId &&
                existingViewModel.Script.Filename == filename &&
                existingViewModel.Notes.Count == viewModel.Notes.Count)
            {
                existingViewModel.Script.SetContent(content);
                viewModel = existingViewModel;

                existingViewModel.SelectedEditor = Game.Editors.FirstOrDefault(e => e.Title == selectedEditor);
                existingViewModel.Script.Editor.MoveCursorTo(line, column, Jamiras.ViewModels.CodeEditor.CodeEditorViewModel.MoveCursorFlags.None);
            }
            else
            {
                if (usingBackup)
                {
                    viewModel.Script.Filename = Path.GetFileName(filename);
                    var title = viewModel.Title + " (from backup)";
                    viewModel.SetValue(GameViewModel.TitleProperty, title);
                }
                else
                {
                    viewModel.Script.Filename = filename;
                }

                viewModel.Script.SetContent(content);
                Game = viewModel;
            }

            if (viewModel.Script.Editor.ErrorsToolWindow.References.Count > 0)
                viewModel.Script.Editor.ErrorsToolWindow.IsVisible = true;
        }

        private void AddRecentFile(string newFile)
        {
            if (_recentFiles.FirstOrDefault() == newFile)
                return;

            if (_recentFiles.FindAndMakeRecent(str => str == newFile) == null)
                _recentFiles.Add(newFile);

            var builder = new StringBuilder();
            foreach (var file in _recentFiles)
            {
                builder.Append(file);
                builder.Append(';');
            }
            builder.Length--;

            var persistance = ServiceRepository.Instance.FindService<IPersistantDataRepository>();
            persistance.SetValue("RecentFiles", builder.ToString());

            RecentFiles = _recentFiles.ToArray();
        }

        private bool SaveScript()
        {
            // if there isn't a path, the user need to select a save location
            if (!Game.Script.Filename.Contains("\\"))
                return SaveScriptAs();

            Game.Script.Save();
            AddRecentFile(Game.Script.Filename);
            return true;
        }

        private bool SaveScriptAs()
        {
            var vm = new FileDialogViewModel();
            vm.DialogTitle = "Save achievements script";
            vm.Filters["Script file"] = "*.rascript;*.txt";
            vm.FileNames = new[] { Game.Script.Filename };
            vm.OverwritePrompt = true;

            if (vm.ShowSaveFileDialog() == DialogResult.Ok)
            {
                Game.Script.Filename = vm.FileNames[0];
                SaveScript();
            }

            return false;
        }

        public CommandBase NewScriptCommand { get; private set; }
        private void NewScript()
        {
            if (!CloseEditor())
                return;

            var dialog = new NewScriptDialogViewModel();
            if (dialog.ShowDialog() == DialogResult.Ok)
                Game = dialog.Finalize();
        }

        public CommandBase UpdateLocalCommand { get; private set; }
        private void UpdateLocal()
        {
            var game = Game;
            if (game == null)
            {
                MessageBoxViewModel.ShowMessage("No game loaded");
                return;
            }

            if (game.Script.Editor.ErrorsToolWindow.References.Count > 0)
            {
                MessageBoxViewModel.ShowMessage("Cannot update while errors exist.");
                game.Script.Editor.ErrorsToolWindow.IsVisible = true;
            }

            var dialog = new UpdateLocalViewModel(game);
            dialog.ShowDialog();
        }

        public static readonly ModelProperty ShowHexValuesProperty = ModelProperty.Register(typeof(MainWindowViewModel), "ShowHexValues", typeof(bool), false, OnShowHexValuesChanged);
        public bool ShowHexValues
        {
            get { return (bool)GetValue(ShowHexValuesProperty); }
            set { SetValue(ShowHexValuesProperty, value); }
        }

        private static void OnShowHexValuesChanged(object sender, ModelPropertyChangedEventArgs e)
        {
            ServiceRepository.Instance.FindService<ISettings>().HexValues = (bool)e.NewValue;
            var vm = (MainWindowViewModel)sender;
            var game = vm.Game;
            if (game != null)
            {
                foreach (var achievement in game.Editors)
                    achievement.OnShowHexValuesChanged(e);
            }
        }

        public CommandBase GameStatsCommand { get; private set; }
        private void GameStats()
        {
            var vm = new GameStatsViewModel();
            vm.ShowDialog();
        }

        public CommandBase OpenTicketsCommand { get; private set; }
        private void OpenTickets()
        {
            var vm = new OpenTicketsViewModel();
            vm.ShowDialog();
        }

        public CommandBase AboutCommand { get; private set; }
        private void About()
        {
            var vm = new AboutDialogViewModel();
            vm.ShowDialog();
        }
    }
}
