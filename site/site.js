document.querySelectorAll("[data-year]").forEach((element) => {
  element.textContent = new Date().getFullYear();
});

const parts = window.location.hostname.split(".");
if (parts.length >= 3 && parts[1] === "github") {
  const owner = parts[0];
  const path = window.location.pathname.split("/").filter(Boolean);
  const repository = path[0] || `${owner}.github.io`;
  const releaseUrl = `https://github.com/${owner}/${repository}/releases/latest`;

  document.querySelectorAll("[data-release-link]").forEach((link) => {
    link.href = releaseUrl;
  });
  const note = document.querySelector("[data-download-note]");
  if (note) {
    note.textContent = "The button opens the latest Regata Windows release. Download the ZIP, extract it, and launch Regata.Desktop.exe.";
  }
}
