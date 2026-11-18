using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Win32;
using FolderPicker = Microsoft.Win32.OpenFolderDialog;

namespace Regata.Desktop;

public partial class MainWindow : Window
{
    private const int PreviewPort = 8787;
    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".md", ".txt", ".svg", ".xml"
    };

    private readonly ObservableCollection<string> _projectFiles = [];
    private readonly Dictionary<string, EditorDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly GroqTutorClient _tutorClient = new();
    private readonly System.Net.Http.HttpClient _httpClient = new();
    private string? _projectFolder;
    private WebApplication? _previewServer;
    private PhysicalFileProvider? _previewFiles;
    private bool _networkPreview;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _projectFiles;
        Closed += MainWindow_Closed;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            Title = "Choose an empty folder for your new website project",
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var folder = Path.GetFullPath(picker.FolderName);
        try
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any())
            {
                MessageBox.Show(this, "Choose an empty folder so Regata won't overwrite anything.",
                    "Folder is not empty", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!PrepareProjectSwitch(folder))
            {
                return;
            }

            File.WriteAllText(Path.Combine(folder, "index.html"), StarterHtml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "styles.css"), StarterCss, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "script.js"), StarterJavaScript, new UTF8Encoding(false));
            LoadProject(folder);
            WorkspaceStatus.Text = "Project created. Open index.html and change the heading to make it yours.";
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not create the project: {exception.Message}";
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            Title = "Open a website project folder",
            Multiselect = false
        };

        if (picker.ShowDialog(this) == true)
        {
            LoadProject(Path.GetFullPath(picker.FolderName));
        }
    }

    private void LoadProject(string folder)
    {
        if (!Directory.Exists(folder))
        {
            WorkspaceStatus.Text = "That project folder no longer exists.";
            return;
        }

        if (!PrepareProjectSwitch(folder))
        {
            return;
        }

        _projectFolder = folder;
        ProjectPathLabel.Text = folder;
        ProjectNameLabel.Text = new DirectoryInfo(folder).Name;
        try
        {
            RefreshProjectFiles();
            WorkspaceStatus.Text = $"Opened {ProjectNameLabel.Text}. Pick a file to start editing.";
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not read the project files: {exception.Message}";
        }
    }

    private bool PrepareProjectSwitch(string folder)
    {
        if (_projectFolder is null
            || string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_projectFolder), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_documents.Values.Any(document => document.IsDirty))
        {
            var result = MessageBox.Show(this,
                "Some open files have unsaved changes. Save them before switching projects?",
                "Unsaved code", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                return false;
            }

            if (result == MessageBoxResult.Yes && !SaveAllDocuments())
            {
                return false;
            }
        }

        _documents.Clear();
        EditorTabs.Items.Clear();
        ActiveFileLabel.Text = "Choose a file from your project";
        return true;
    }

    private void RefreshProjectFiles(string? selectRelativePath = null)
    {
        if (_projectFolder is null)
        {
            return;
        }

        var currentFile = selectRelativePath ?? GetCurrentDocument()?.RelativePath;
        var files = new List<string>();
        CollectProjectFiles(_projectFolder, files);
        _projectFiles.Clear();
        foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            _projectFiles.Add(file);
        }

        if (currentFile is not null)
        {
            FileList.SelectedItem = _projectFiles.FirstOrDefault(path =>
                string.Equals(path, currentFile, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void CollectProjectFiles(string folder, ICollection<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (TextFileExtensions.Contains(Path.GetExtension(file)))
            {
                files.Add(Path.GetRelativePath(folder, file));
            }
        }

        foreach (var childFolder in Directory.EnumerateDirectories(folder))
        {
            var directory = new DirectoryInfo(childFolder);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
                || directory.Name is ".git" or "node_modules" or "bin" or "obj" or ".vs")
            {
                continue;
            }

            CollectProjectFiles(childFolder, files);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_projectFolder is null || FileList.SelectedItem is not string relativePath)
        {
            return;
        }

        try
        {
            var filePath = Path.GetFullPath(Path.Combine(_projectFolder, relativePath));
            var projectRoot = GetProjectRootWithSeparator();
            if (!filePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                WorkspaceStatus.Text = "That file is outside the open project folder.";
                return;
            }

            OpenFile(filePath, relativePath);
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not open that file: {exception.Message}";
        }
    }

    private void OpenFile(string filePath, string relativePath)
    {
        if (_documents.TryGetValue(filePath, out var existingDocument))
        {
            EditorTabs.SelectedItem = existingDocument.Tab;
            return;
        }

        var editor = new TextBox
        {
            Text = File.ReadAllText(filePath),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = (System.Windows.Media.Brush)FindResource("EditorTextBrush"),
            CaretBrush = (System.Windows.Media.Brush)FindResource("YellowBrush"),
            SelectionBrush = (System.Windows.Media.Brush)FindResource("SelectionBrush"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 14, 16, 14),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14
        };
        SpellCheck.SetIsEnabled(editor, false);

        var tab = new TabItem { Header = relativePath, Content = editor };
        var document = new EditorDocument(filePath, relativePath, tab, editor);
        tab.Tag = document;
        editor.TextChanged += (_, _) => MarkDocumentDirty(document);
        _documents.Add(filePath, document);
        EditorTabs.Items.Add(tab);
        EditorTabs.SelectedItem = tab;
        WorkspaceStatus.Text = $"Opened {relativePath}. Press Ctrl+S to save your changes.";
    }

    private void MarkDocumentDirty(EditorDocument document)
    {
        document.IsDirty = true;
        document.Tab.Header = $"{document.RelativePath} •";
        ActiveFileLabel.Text = $"{document.RelativePath}  ·  unsaved changes";
    }

    private EditorDocument? GetCurrentDocument()
    {
        return EditorTabs.SelectedItem is TabItem { Tag: EditorDocument document } ? document : null;
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender != EditorTabs)
        {
            return;
        }

        var document = GetCurrentDocument();
        ActiveFileLabel.Text = document is null
            ? (_projectFolder is null ? "Create or open a project to start coding" : "Choose a file from your project")
            : document.IsDirty ? $"{document.RelativePath}  ·  unsaved changes" : document.RelativePath;
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_projectFolder is null)
        {
            WorkspaceStatus.Text = "Create or open a project before adding files.";
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "Create a file in your project",
            InitialDirectory = _projectFolder,
            FileName = "new-file.html",
            Filter = "Web and text files|*.html;*.htm;*.css;*.js;*.mjs;*.json;*.md;*.txt|All files|*.*",
            AddExtension = false,
            OverwritePrompt = false
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var selectedPath = Path.GetFullPath(picker.FileName);
        if (!selectedPath.StartsWith(GetProjectRootWithSeparator(), StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceStatus.Text = "New files must be inside the open project folder.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
            using (File.Create(selectedPath))
            {
            }

            var relativePath = Path.GetRelativePath(_projectFolder, selectedPath);
            RefreshProjectFiles(relativePath);
            OpenFile(selectedPath, relativePath);
            WorkspaceStatus.Text = $"Created {relativePath}. Try writing one small thing first.";
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not create that file: {exception.Message}";
        }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentDocument();
    }

    private bool SaveCurrentDocument()
    {
        var document = GetCurrentDocument();
        if (document is null || !document.IsDirty)
        {
            WorkspaceStatus.Text = document is null ? "Choose a file to edit first." : "All changes are saved.";
            return document is not null;
        }

        return SaveDocument(document);
    }

    private bool SaveAllDocuments()
    {
        var activeDocument = GetCurrentDocument();
        foreach (var document in _documents.Values.Where(document => document.IsDirty))
        {
            if (!SaveDocument(document))
            {
                return false;
            }
        }

        if (activeDocument is not null)
        {
            EditorTabs.SelectedItem = activeDocument.Tab;
        }

        return true;
    }

    private bool SaveDocument(EditorDocument document)
    {
        try
        {
            File.WriteAllText(document.FilePath, document.Editor.Text, new UTF8Encoding(false));
            document.IsDirty = false;
            document.Tab.Header = document.RelativePath;
            if (ReferenceEquals(GetCurrentDocument(), document))
            {
                ActiveFileLabel.Text = document.RelativePath;
            }

            WorkspaceStatus.Text = $"Saved {document.RelativePath}.";
            return true;
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not save {document.RelativePath}: {exception.Message}";
            return false;
        }
    }

    private string GetProjectRootWithSeparator()
    {
        return Path.GetFullPath(_projectFolder!)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            SaveCurrentDocument();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Preview_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_projectFolder is null || !Directory.Exists(_projectFolder))
        {
            WorkspaceStatus.Text = "Open a project before running a command.";
            return;
        }

        var command = TerminalInput.Text.Trim();
        if (command.Length == 0)
        {
            WorkspaceStatus.Text = "Type a command to run from your project folder.";
            return;
        }

        TerminalInput.Clear();
        TerminalOutput.Text = $"> {command}{Environment.NewLine}Running…";
        WorkspaceStatus.Text = "Running your command in the project folder…";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = _projectFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdoutTask;
            var error = await stderrTask;
            var result = new StringBuilder($"> {command}{Environment.NewLine}");
            if (!string.IsNullOrWhiteSpace(output))
            {
                result.AppendLine(output.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                result.AppendLine(error.TrimEnd());
            }

            result.AppendLine($"Process exited with code {process.ExitCode}.");
            TerminalOutput.Text = result.ToString();
            WorkspaceStatus.Text = process.ExitCode == 0
                ? "Command finished."
                : $"Command finished with exit code {process.ExitCode}.";
        }
        catch (Exception exception)
        {
            TerminalOutput.Text = $"> {command}{Environment.NewLine}{exception.Message}";
            WorkspaceStatus.Text = $"Could not run that command: {exception.Message}";
        }
    }

    private void TerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunCommand_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllDocuments() || !SaveCurrentDocumentForPreview())
        {
            return;
        }

        if (_previewServer is null && !await StartPreviewServerAsync(networkShare: false))
        {
            return;
        }

        OpenUrl($"http://localhost:{PreviewPort}/");
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_previewServer is not null)
        {
            if (_networkPreview)
            {
                await StopPreviewServerAsync();
                return;
            }

            await StopPreviewServerAsync();
            if (_previewServer is not null)
            {
                return;
            }
        }

        if (!SaveAllDocuments() || !SaveCurrentDocumentForPreview())
        {
            return;
        }

        var confirm = MessageBox.Show(this,
            "Network sharing has no password. Only share on a network you trust. Continue?",
            "Share a preview on your network", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (await StartPreviewServerAsync(networkShare: true))
        {
            ShareButtonState(true);
            var address = GetNetworkPreviewAddress();
            WorkspaceStatus.Text = $"Preview shared on your network at {address}";
            OpenUrl(address);
        }
    }

    private bool SaveCurrentDocumentForPreview()
    {
        if (_projectFolder is null)
        {
            WorkspaceStatus.Text = "Create or open a project before starting a preview.";
            return false;
        }

        var activeDocument = GetCurrentDocument();
        if (activeDocument is { IsDirty: true })
        {
            return SaveCurrentDocument();
        }

        if (!File.Exists(Path.Combine(_projectFolder, "index.html")))
        {
            WorkspaceStatus.Text = "Add an index.html file to your project before previewing it.";
            return false;
        }

        return true;
    }

    private async Task<bool> StartPreviewServerAsync(bool networkShare)
    {
        if (_projectFolder is null)
        {
            return false;
        }

        WebApplication? application = null;
        PhysicalFileProvider? files = null;
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (networkShare)
                {
                    options.ListenAnyIP(PreviewPort);
                }
                else
                {
                    options.ListenLocalhost(PreviewPort);
                }
            });

            application = builder.Build();
            files = new PhysicalFileProvider(_projectFolder);
            application.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            application.Use(async (context, next) =>
            {
                var segments = context.Request.Path.Value?
                    .Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)
                    || IsSensitiveFileName(segment)))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next();
            });
            application.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            await application.StartAsync();

            _previewServer = application;
            _previewFiles = files;
            _networkPreview = networkShare;
            application = null;
            files = null;
            PreviewStatus.Text = networkShare ? "Network preview running" : "Local preview running";
            WorkspaceStatus.Text = networkShare
                ? "Your preview is available on the trusted local network."
                : "Your preview opened in your browser.";
            return true;
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not start the preview: {exception.Message}";
            if (application is not null)
            {
                await application.DisposeAsync();
            }

            files?.Dispose();
            return false;
        }
    }

    private async Task StopPreviewServerAsync()
    {
        if (_previewServer is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _previewServer.StopAsync(timeout.Token);
            await _previewServer.DisposeAsync();
            _previewFiles?.Dispose();
            _previewFiles = null;
            _previewServer = null;
            _networkPreview = false;
            ShareButtonState(false);
            PreviewStatus.Text = "Preview stopped";
            WorkspaceStatus.Text = "Your project is no longer being shared.";
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"Could not stop the preview cleanly: {exception.Message}";
        }
    }

    private void ShareButtonState(bool isSharing)
    {
        ShareButton.Content = isSharing ? "Stop sharing" : "Share on Wi-Fi";
    }

    private void OpenUrl(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            WorkspaceStatus.Text = $"The preview is running at {address}, but the browser could not be opened: {exception.Message}";
        }
    }

    private string GetNetworkPreviewAddress()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(item.Address))
            .Select(item => item.Address.ToString())
            .FirstOrDefault();

        return address is null ? $"http://localhost:{PreviewPort}/" : $"http://{address}:{PreviewPort}/";
    }

    private static bool IsSensitiveFileName(string fileName)
    {
        return fileName.Equals("web.config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("credentials", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("secrets", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".secret", StringComparison.OrdinalIgnoreCase);
    }

    private void ConnectTutor_Click(object sender, RoutedEventArgs e)
    {
        var keyWindow = new Window
        {
            Title = "Connect your Groq tutor",
            Owner = this,
            Width = 480,
            Height = 290,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "Connect your Groq tutor",
            FontSize = 21,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Create a fresh Groq API key and enter it here. Regata stores it in Windows Credential Manager, not in your project or code.",
            Margin = new Thickness(0, 9, 0, 15),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
        });
        var keyInput = new PasswordBox
        {
            Padding = new Thickness(9),
            BorderBrush = (System.Windows.Media.Brush)FindResource("InkBrush")
        };
        panel.Children.Add(keyInput);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 17, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => keyWindow.Close();
        var forget = new Button
        {
            Content = "Forget saved key",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        forget.Click += (_, _) =>
        {
            try
            {
                GroqCredentialStore.Delete();
                TutorStatus.Text = "Saved Groq key removed from this Windows account.";
                keyWindow.Close();
                WorkspaceStatus.Text = "Groq key forgotten.";
            }
            catch (Exception exception)
            {
                MessageBox.Show(keyWindow, exception.Message, "Could not remove the key",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        var save = new Button { Content = "Save key securely", Style = (Style)FindResource("ActionButton") };
        save.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(keyInput.Password))
                {
                    throw new InvalidOperationException("Enter a Groq API key first.");
                }

                GroqCredentialStore.Save(keyInput.Password);
                TutorStatus.Text = "Groq key saved securely on this Windows account.";
                keyWindow.Close();
                WorkspaceStatus.Text = "Tutor connected. Your key stays in Windows Credential Manager.";
            }
            catch (Exception exception)
            {
                MessageBox.Show(keyWindow, exception.Message, "Could not save the key",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(forget);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        keyWindow.Content = panel;
        keyWindow.ShowDialog();
    }

    private async void AskTutor_Click(object sender, RoutedEventArgs e)
    {
        var prompt = TutorPromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            TutorStatus.Text = "Ask a question to get a helpful hint.";
            return;
        }

        string? apiKey;
        try
        {
            apiKey = GroqCredentialStore.Read();
        }
        catch (Exception exception)
        {
            TutorStatus.Text = $"Could not read the saved key: {exception.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            TutorStatus.Text = "Connect Groq before asking your tutor.";
            return;
        }

        var currentDocument = GetCurrentDocument();
        var codeContext = IncludeFileCheckBox.IsChecked == true && currentDocument is not null
            ? currentDocument.Editor.Text
            : null;

        AskTutorButton.IsEnabled = false;
        TutorStatus.Text = codeContext is null
            ? "Sending your question to Groq…"
            : "Sending your question and selected file to Groq…";
        try
        {
            var answer = await _tutorClient.GetGuidanceAsync(_httpClient, apiKey, prompt, codeContext);
            TutorTranscript.AppendText($"{Environment.NewLine}{Environment.NewLine}YOU: {prompt}{Environment.NewLine}{Environment.NewLine}TUTOR: {answer}");
            TutorTranscript.ScrollToEnd();
            TutorPromptBox.Clear();
            TutorStatus.Text = "Got a hint. You’re still in charge of the code.";
        }
        catch (Exception exception)
        {
            TutorStatus.Text = $"Tutor request failed: {exception.Message}";
        }
        finally
        {
            AskTutorButton.IsEnabled = true;
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_previewServer is not null && !_isClosing)
        {
            _isClosing = true;
            await StopPreviewServerAsync();
        }
    }

    private const string StarterHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>My first Regata project</title>
          <link rel="stylesheet" href="styles.css">
        </head>
        <body>
          <main>
            <h1>Hello, world!</h1>
            <p>Change this page, save it, and press Preview to see what happens.</p>
            <button id="try-it">Try me</button>
          </main>
          <script src="script.js"></script>
        </body>
        </html>
        """;

    private const string StarterCss = """
        body {
          margin: 3rem;
          background: #fff9ec;
          color: #26364c;
          font: 1.1rem system-ui, sans-serif;
        }

        main {
          max-width: 42rem;
          margin: 10vh auto;
        }

        button {
          padding: .7rem 1rem;
          border: 2px solid #26364c;
          border-radius: .5rem;
          background: #ffd332;
          font: inherit;
        }
        """;

    private const string StarterJavaScript = """
        const button = document.querySelector("#try-it");

        button.addEventListener("click", () => {
          button.textContent = "You changed the page with JavaScript!";
        });
        """;

    private sealed class EditorDocument(string filePath, string relativePath, TabItem tab, TextBox editor)
    {
        public string FilePath { get; } = filePath;
        public string RelativePath { get; } = relativePath;
        public TabItem Tab { get; } = tab;
        public TextBox Editor { get; } = editor;
        public bool IsDirty { get; set; }
    }
}
