﻿// FFXIV TexTools
// Copyright © 2019 Rafael Gonzalez - All Rights Reserved
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License

using FFXIV_TexTools.Helpers;
using FFXIV_TexTools.Resources;
using FFXIV_TexTools.ViewModels;
using MahApps.Metro.Controls.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Media;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Mods;
using xivModdingFramework.Mods.DataContainers;
using xivModdingFramework.Mods.Enums;
using xivModdingFramework.Mods.FileTypes;
using xivModdingFramework.SqPack.FileTypes;
using xivModdingFramework.Textures.Enums;
using Path = System.IO.Path;

namespace FFXIV_TexTools.Views
{
    /// <summary>
    /// Interaction logic for SimpleModPackImporter.xaml
    /// </summary>
    public partial class SimpleModPackImporter
    {
        public ObservableCollection<SimpleModpackEntry> Entries { get; set; } = new ObservableCollection<SimpleModpackEntry>();
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;
        private readonly DirectoryInfo _gameDirectory, _modPackDirectory;
        private ProgressDialogController _progressController;
        private readonly TTMP _texToolsModPack;
        private long _modSize;
        private bool _messageInImport, _indexLockStatus;
        private TextureViewModel _textureViewModel;
        private ModelViewModel _modelViewModel;
        private ModPackJson _packJson;
        private bool _silent = false;

        [DllImport("Shlwapi.dll", CharSet = CharSet.Auto)]
        public static extern long StrFormatByteSize(long fileSize, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder buffer, int bufferSize);

        public List<ModsJson> JsonEntries;
        public HashSet<int> SelectedEntries;

        public long ModSize
        {
            get
            {
                return _modSize;
            }
            set
            {
                _modSize = value;
                var byteFormatedString = new StringBuilder(32);
                StrFormatByteSize(_modSize, byteFormatedString, byteFormatedString.Capacity);

                ModCountLabel.Content = SelectedEntries.Count;
                ModSizeLabel.Content = byteFormatedString.ToString();

                if (!_indexLockStatus)
                {
                    ImportModPackButton.IsEnabled = SelectedEntries.Count > 0;
                }

            }
        }


        public SimpleModPackImporter(DirectoryInfo modPackDirectory, ModPackJson modPackJson, TextureViewModel textureViewModel, ModelViewModel modelViewModel, bool silent = false, bool messageInImport = false)
        {
            this.DataContext = this;

            InitializeComponent();

            JsonEntries = new List<ModsJson>();
            SelectedEntries = new HashSet<int>();

            _modPackDirectory = modPackDirectory;
            _gameDirectory = new DirectoryInfo(Properties.Settings.Default.FFXIV_Directory);
            _texToolsModPack = new TTMP(new DirectoryInfo(Properties.Settings.Default.ModPack_Directory),
                XivStrings.TexTools);
            _messageInImport = messageInImport;
            _textureViewModel = textureViewModel;
            _modelViewModel = modelViewModel;

            var index = new Index(_gameDirectory);

            _indexLockStatus = index.IsIndexLocked(XivDataFile._0A_Exd);


            _packJson = modPackJson;
            _silent = silent;

            ModListView.IsEnabled = false;
            LockedStatusLabel.Foreground = Brushes.Black;
            LockedStatusLabel.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            LockedStatusLabel.Content = UIStrings.Loading;

            Task.Run(Initialize);
        }

        #region Public Properties


        /// <summary>
        /// Total mods imported
        /// </summary>
        public int TotalModsImported { get; private set; }

        #endregion

        #region Private Methods
        private async void Initialize()
        {
            if (_packJson != null)
            {
                await ImportSimpleModPack(_packJson);
            }
            else
            {
                await ImportOldModPack();
            }

            Dispatcher.Invoke(() =>
            {

                // Resize columns to fit content
                foreach (var column in GridViewCol.Columns)
                {
                    if (double.IsNaN(column.Width))
                    {
                        column.Width = column.ActualWidth;
                    }

                    column.Width = double.NaN;
                }

                LockedStatusLabel.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Right;
                LockedStatusLabel.Foreground = Brushes.Red;
                LockedStatusLabel.Content = string.Empty;
                ModListView.IsEnabled = true;

                if (_indexLockStatus)
                {
                    LockedStatusLabel.Content = UIStrings.Index_Locked;
                }

                if (_silent)
                {
                    FinalizeImport();
                }
            });
        }

        /// <summary>
        /// Imports a simple mod pack
        /// </summary>
        /// <param name="modPackJson">The mod pack json</param>
        private async Task ImportSimpleModPack(ModPackJson modPackJson)
        {
            var modding = new Modding(_gameDirectory);
            Dispatcher.Invoke(() =>
            {
                // This does not need to be an async task set.
                for (int i = 0; i < modPackJson.SimpleModsList.Count; i++)
                {
                    var jsonItem = modPackJson.SimpleModsList[i];

                    // For some reason the ModPackEntry was never set before 2.0.13 so this is necessary for modpacks created prior to then
                    if (jsonItem.ModPackEntry == null)
                    {
                        // Manually add the modpack entry that this mod is a part of
                        jsonItem.ModPackEntry = new ModPack
                        {
                            name = modPackJson.Name,
                            author = modPackJson.Author,
                            version = modPackJson.Version
                        };
                    }

                    JsonEntries.Add(jsonItem);
                    Entries.Add(new SimpleModpackEntry(JsonEntries.Count - 1, this));
                }

                ModPackName.Content = modPackJson.Name;
                ModPackAuthor.Content = modPackJson.Author;
                ModPackVersion.Content = modPackJson.Version;

                var cv = (CollectionView)CollectionViewSource.GetDefaultView(ModListView.ItemsSource);
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(new SortDescription(nameof(SimpleModpackEntry.Name), _lastDirection));

                SelectedEntries.Clear();
                long size = 0;
                for (int i = 0; i < JsonEntries.Count; i++)
                {
                    SelectedEntries.Add(i);
                    size += JsonEntries[i].ModSize;
                }
                ModListView.SelectAll();
                ModSize = size;
            });
        }

        /// <summary>
        /// Imports a first generation mod pack
        /// </summary>
        /// <param name="modPackDirectory">The mod pack directory</param>
        private async Task ImportOldModPack()
        {
            Dispatcher.Invoke(() =>
            {
                var progress = new Progress<(int count, int total)>(prog =>
                {
                LockedStatusLabel.Content = $"{UIStrings.Loading} ({prog.count}, {prog.total})";

                if (prog.count == prog.total)
                {
                    LockedStatusLabel.Content = UIStrings.Finalizing;

                }
                });
            });
            var modding = new Modding(_gameDirectory);

            var originalModPackData = await _texToolsModPack.GetOriginalModPackJsonData(_modPackDirectory);

            Dispatcher.Invoke(() =>
            {
                // There is nearly no point to doing this on another thread if it's going to be constantly
                // re-invoking the main thread with literally every line.
                foreach (var modsJson in originalModPackData)
                {
                    var jsonEntry = new ModsJson
                    {
                        Name = modsJson.Name,
                        Category = modsJson.Category.GetDisplayName(),
                        FullPath = modsJson.FullPath,
                        DatFile = modsJson.DatFile,
                        ModOffset = modsJson.ModOffset,
                        ModSize = modsJson.ModSize,
                        ModPackEntry = new ModPack
                        {
                            name = Path.GetFileNameWithoutExtension(_modPackDirectory.FullName),
                            author = "N/A",
                            version = "1.0.0"
                        }
                    };
                    JsonEntries.Add(jsonEntry);
                    Entries.Add(new SimpleModpackEntry(JsonEntries.Count - 1, this));

                }

                ModPackName.Content = Path.GetFileNameWithoutExtension(_modPackDirectory.FullName);
                ModPackAuthor.Content = "N/A";
                ModPackVersion.Content = "1.0.0";

                var cv = (CollectionView)CollectionViewSource.GetDefaultView(ModListView.ItemsSource);
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(new SortDescription(nameof(SimpleModpackEntry.Name), _lastDirection));

                ModListView.SelectAll();
            });
        }

        /// <summary>
        /// Gets the race from the path
        /// </summary>
        /// <param name="modPath">The mod path</param>
        /// <returns>The race as XivRace</returns>
        private Task<XivRace> GetRace(string modPath)
        {
            return Task.Run(() =>
            {
                var xivRace = XivRace.All_Races;

                if (modPath.Contains("ui/") || modPath.Contains(".avfx"))
                {
                    xivRace = XivRace.All_Races;
                }
                else if (modPath.Contains("monster"))
                {
                    xivRace = XivRace.Monster;
                }
                else if (modPath.Contains("bgcommon"))
                {
                    xivRace = XivRace.All_Races;
                }
                else if (modPath.Contains(".tex") || modPath.Contains(".mdl") || modPath.Contains(".atex"))
                {
                    if (modPath.Contains("accessory") || modPath.Contains("weapon") || modPath.Contains("/common/"))
                    {
                        xivRace = XivRace.All_Races;
                    }
                    else
                    {
                        if (modPath.Contains("demihuman"))
                        {
                            xivRace = XivRace.DemiHuman;
                        }
                        else if (modPath.Contains("/v"))
                        {
                            var raceCode = modPath.Substring(modPath.IndexOf("_c") + 2, 4);
                            xivRace = XivRaces.GetXivRace(raceCode);
                        }
                        else
                        {
                            var raceCode = modPath.Substring(modPath.IndexOf("/c") + 2, 4);
                            xivRace = XivRaces.GetXivRace(raceCode);
                        }
                    }

                }

                return xivRace;
            });
        }

        /// <summary>
        /// Gets the number from the path
        /// </summary>
        /// <param name="modPath">The mod path</param>
        /// <returns>The number</returns>
        private Task<string> GetNumber(string modPath)
        {
            return Task.Run(() =>
            {
                var number = "-";

                if (modPath.Contains("/human/") && modPath.Contains("/body/"))
                {
                    var subString = modPath.Substring(modPath.LastIndexOf("/b") + 2, 4);
                    number = int.Parse(subString).ToString();
                }

                if (modPath.Contains("/face/"))
                {
                    var subString = modPath.Substring(modPath.LastIndexOf("/f") + 2, 4);
                    number = int.Parse(subString).ToString();
                }

                if (modPath.Contains("decal_face"))
                {
                    var length = modPath.LastIndexOf(".") - (modPath.LastIndexOf("_") + 1);
                    var subString = modPath.Substring(modPath.LastIndexOf("_") + 1, length);

                    number = int.Parse(subString).ToString();
                }

                if (modPath.Contains("decal_equip"))
                {
                    var subString = modPath.Substring(modPath.LastIndexOf("_") + 1, 3);

                    try
                    {
                        number = int.Parse(subString).ToString();
                    }
                    catch
                    {
                        if (modPath.Contains("stigma"))
                        {
                            number = "stigma";
                        }
                        else
                        {
                            number = "Error";
                        }
                    }
                }

                if (modPath.Contains("/hair/"))
                {
                    var t = modPath.Substring(modPath.LastIndexOf("/h") + 2, 4);
                    number = int.Parse(t).ToString();
                }

                if (modPath.Contains("/tail/"))
                {
                    var t = modPath.Substring(modPath.LastIndexOf("l/t") + 3, 4);
                    number = int.Parse(t).ToString();
                }

                return number;
            });
        }

        /// <summary>
        /// Gets the type from the path
        /// </summary>
        /// <param name="modPath">The mod path</param>
        /// <returns>The type</returns>
        private Task<string> GetType(string modPath)
        {
            return Task.Run(() =>
            {
                var type = "-";

                if (modPath.Contains(".tex") || modPath.Contains(".mdl") || modPath.Contains(".atex"))
                {
                    if (modPath.Contains("demihuman"))
                    {
                        type = slotAbr[modPath.Substring(modPath.LastIndexOf("/") + 16, 3)];
                    }

                    if (modPath.Contains("/face/"))
                    {
                        if (modPath.Contains(".tex"))
                        {
                            var fileName = Path.GetFileNameWithoutExtension(modPath);

                            try
                            {
                                type = FaceTypes[fileName.Substring(fileName.IndexOf("_") + 1, 3)];
                            } catch
                            {
                                type = "Unknown";
                            }
                        }
                    }

                    if (modPath.Contains("/hair/"))
                    {
                        if (modPath.Contains(".tex"))
                        {
                            var fileName = Path.GetFileNameWithoutExtension(modPath);
                            try {
                                type = HairTypes[fileName.Substring(fileName.IndexOf("_") + 1, 3)];
                            } catch
                            {
                                type = "Unknown";
                            }
                    }
                    }

                    if (modPath.Contains("/vfx/"))
                    {
                        type = "VFX";
                    }

                }
                else if (modPath.Contains(".avfx"))
                {
                    type = "AVFX";
                }

                return type;
            });
        }

        /// <summary>
        /// Gets the part from the path
        /// </summary>
        /// <param name="modPath">The mod path</param>
        /// <returns>The part</returns>
        private Task<string> GetPart(string modPath)
        {
            return Task.Run(() =>
            {
                var part = "-";
                var parts = new[] { "a", "b", "c", "d", "e", "f" };

                if (modPath.Contains("/equipment/") || modPath.Contains("/hair/"))
                {
                    if (modPath.Contains("/texture/"))
                    {
                        part = modPath.Substring(modPath.LastIndexOf("_") - 1, 1);
                        foreach (var letter in parts)
                        {
                            if (part == letter) return part;
                        }
                        return "a";
                    }

                    if (modPath.Contains("/material/"))
                    {
                        return modPath.Substring(modPath.LastIndexOf("_") + 1, 1);
                    }
                }

                return part;
            });
        }

        /// <summary>
        /// Gets the map from the path
        /// </summary>
        /// <param name="modPath">The mod path</param>
        /// <returns>The map</returns>
        private Task<string> GetMap(string modPath)
        {
            return Task.Run(() =>
            {
                var xivTexType = XivTexType.Other;

                if (modPath.Contains(".mdl"))
                {
                    return "3D";
                }

                if (modPath.Contains(".mtrl"))
                {
                    return "ColorSet";
                }

                if (modPath.Contains("ui/"))
                {
                    var subString = modPath.Substring(modPath.IndexOf("/") + 1);
                    return subString.Substring(0, subString.IndexOf("/"));
                }

                if (modPath.Contains("_s.tex") || modPath.Contains("skin_m"))
                {
                    xivTexType = XivTexType.Specular;
                }
                else if (modPath.Contains("_d.tex"))
                {
                    xivTexType = XivTexType.Diffuse;
                }
                else if (modPath.Contains("_n.tex"))
                {
                    xivTexType = XivTexType.Normal;
                }
                else if (modPath.Contains("_m.tex"))
                {
                    xivTexType = XivTexType.Multi;
                }
                else if (modPath.Contains(".atex"))
                {
                    var atex = Path.GetFileNameWithoutExtension(modPath);
                    return atex.Substring(0, 4);
                }
                else if (modPath.Contains("decal"))
                {
                    xivTexType = XivTexType.Mask;
                }

                return xivTexType.ToString();
            });
        }

        /// <summary>
        /// Updates the progress bar
        /// </summary>
        /// <param name="value">The progress value</param>
        private void ReportProgress((int current, int total, string message) report)
        {
            if (!report.message.Equals(string.Empty))
            {
                _progressController.SetMessage(report.message);
                _progressController.SetIndeterminate();
            }
            else
            {
                _progressController.SetMessage(
                    $"{UIMessages.PleaseStandByMessage} ({report.current} / {report.total})");

                var value = (double)report.current / (double)report.total;
                _progressController.SetProgress(value);
            }
        }

        /// <summary>
        /// Writes all selected mods to game data
        /// </summary>
        private async void FinalizeImport()
        {
            _progressController = await this.ShowProgressAsync(UIMessages.ModPackImportTitle, UIMessages.PleaseStandByMessage);

            var importList = SelectedEntries.Select(x => JsonEntries[x]).ToList();

            var modListDirectory = new DirectoryInfo(Path.Combine(_gameDirectory.Parent.Parent.FullName, XivStrings.ModlistFilePath));

            var progressIndicator = new Progress<(int current, int total, string message)>(ReportProgress);

            try
            {
                var importResults = await _texToolsModPack.ImportModPackAsync(_modPackDirectory, importList,
                    _gameDirectory, modListDirectory, progressIndicator);

                TotalModsImported = importResults.ImportCount;

                if (!string.IsNullOrEmpty(importResults.Errors))
                {
                    FlexibleMessageBox.Show(
                        $"{UIMessages.ErrorImportingModsMessage}\n\n{importResults.Errors}", UIMessages.ErrorImportingModsTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                FlexibleMessageBox.Show(
                    $"{UIMessages.ErrorImportingModsMessage}\n\n{ex.Message}", UIMessages.ErrorImportingModsTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            await _progressController.CloseAsync();

            if (_messageInImport)
            {
                await this.ShowMessageAsync(UIMessages.ImportCompleteTitle,
                    string.Format(UIMessages.SuccessfulImportCountMessage, TotalModsImported));
            }

            // When the import is done force an update of the Texture/Model tabs by setting the selected parts
            if (_textureViewModel != null && _modelViewModel != null)
            {
                if (_textureViewModel.SelectedPart != null)
                {
                    _textureViewModel.SelectedPart = _textureViewModel.SelectedPart;
                }
                if (_modelViewModel.SelectedPart != null)
                {
                    _modelViewModel.SelectedPart = _modelViewModel.SelectedPart;
                }
            }

            DialogResult = true;
        }

        #endregion



        #region Event Handlers

        /// <summary>
        /// Event handler for the mod list selection changed
        /// </summary>
        private void ModListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // This is called whenever an item is simply selected; not the checkbox enabled.
            // We don't actually want to do anything here.  This is called with removed items
            // any time the search window is used to filter things out.
        }

        /// <summary>
        /// Event handler for the import mod pack button clicked
        /// </summary>
        private void ImportModPackButton_Click(object sender, RoutedEventArgs e)
        {
            FinalizeImport();
        }

        /// <summary>
        /// The event handler for one of the headers in thea list being clicked
        /// </summary>
        private void Header_Click(object sender, RoutedEventArgs e)
        {
            _lastDirection = _lastDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;

            if (e.OriginalSource is GridViewColumnHeader h && h.Content != null)
            {
                var cv = (CollectionView)CollectionViewSource.GetDefaultView(ModListView.ItemsSource);
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(new SortDescription(h.Content.ToString(), _lastDirection));
            }
        }

        /// <summary>
        /// Event handler for the select all button clicked
        /// </summary>
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            long newSize = 0;
            for (int i = 0; i < JsonEntries.Count; i++)
            {
                SelectedEntries.Add(i);
                newSize += JsonEntries[i].ModSize;
            }
            foreach (var entry in Entries)
            {
                entry.MarkDirty();
            }
            ModSize = newSize;
            ModListView.Focus();
        }

        /// <summary>
        /// Event handler for the clear selected button clicked
        /// </summary>
        private void ClearSelectedButton_Click(object sender, RoutedEventArgs e)
        {

            SelectedEntries.Clear();
            foreach (var entry in Entries)
            {
                entry.MarkDirty();
            }
            ModSize = 0;
            ModListView.Focus();
        }

        /// <summary>
        /// Event handler for the cancel button clicked
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion


        private static readonly Dictionary<string, string> FaceTypes = new Dictionary<string, string>
        {
            {"fac", XivStrings.Face},
            {"iri", XivStrings.Iris},
            {"etc", XivStrings.Etc},
            {"acc", XivStrings.Accessory}
        };

        private static readonly Dictionary<string, string> HairTypes = new Dictionary<string, string>
        {
            {"acc", XivStrings.Accessory},
            {"hir", XivStrings.Hair},
        };

        private static readonly Dictionary<string, string> slotAbr = new Dictionary<string, string>
        {
            {"met", XivStrings.Head},
            {"glv", XivStrings.Hands},
            {"dwn", XivStrings.Legs},
            {"sho", XivStrings.Feet},
            {"top", XivStrings.Body},
            {"ear", XivStrings.Ears},
            {"nek", XivStrings.Neck},
            {"rir", XivStrings.Ring_Right},
            {"ril", XivStrings.Ring_Left},
            {"wrs", XivStrings.Wrists},
        };

    }
}
