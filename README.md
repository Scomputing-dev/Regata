# Regata

Regata is a Windows-first, beginner-friendly web coding workspace. Write HTML, CSS, and JavaScript yourself, see a live preview, run commands, and ask a Groq-powered tutor for hints and explanations instead of having it silently build the project for you.

## Run the desktop app

Install the .NET 10 SDK and run:

```powershell
dotnet run --project desktop\Regata.Desktop.csproj
```

Create or open a project folder. Regata edits files in place, previews `index.html`, and offers an optional local-network preview. Terminal commands run only after you submit them.

### Groq tutor setup

Create a **new** Groq API key after revoking any key that was pasted into chat. Add it using **Connect Groq** inside Regata. The app stores the key in Windows Credential Manager; it is not written to your project, source files, or Git history. Tutor questions and, only when you opt in, the current file are sent to Groq. Regata does not use a local AI model.

## Publish the homepage

Push this repository to a GitHub repository with the default branch named `main`. In **Settings → Pages**, choose **GitHub Actions** as the deployment source. The workflow at `.github/workflows/pages.yml` publishes the `site` directory.

## Publish a Windows release

Create and push a version tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow at `.github/workflows/release.yml` builds a self-contained, compressed `Regata.Desktop.exe` and also attaches `Regata-Windows-x64.zip` as an alternative. The website's download button downloads the executable directly from the latest GitHub release.
