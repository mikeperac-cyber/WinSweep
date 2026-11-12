// The six pages of the app, each with the ambient background colour the app itself
// uses for that page (MainWindow.PageColors), so the walkthrough matches the product.
const PAGES = {
  status: {
    title: "Status",
    ambient: "#342f1c",
    caption: "A read-only snapshot: a health score from processor and memory pressure plus free space on the system drive.",
    alt: "The WinSweep Status page: a health score ring, tiles for processor, memory, storage and battery, drive bars and the busiest processes."
  },
  clean: {
    title: "Clean",
    ambient: "#1b2940",
    caption: "21 known locations, sized and grouped, with nothing selected until you review it.",
    alt: "The WinSweep Clean page listing disposable caches with their sizes and a note that nothing is selected yet."
  },
  apps: {
    title: "Apps",
    ambient: "#392228",
    caption: "Every installed application with its source, version and measured size. Opening one shows its uninstaller and leftovers.",
    alt: "The WinSweep Apps page listing installed applications with version, source and measured size."
  },
  projects: {
    title: "Projects",
    ambient: "#1c2b29",
    caption: "Six rebuildable folders found in this project, all kept because each changed within the last seven days.",
    alt: "The WinSweep Project artifacts page showing six measured candidates and the reason each one was kept."
  },
  optimize: {
    title: "Optimize",
    ambient: "#2e2d2c",
    caption: "Seven single-purpose tasks. Each states what it changes and how long it may take before you run it.",
    alt: "The WinSweep Maintenance page listing bounded maintenance tasks with their effects."
  },
  analyze: {
    title: "Analyze",
    ambient: "#3e2719",
    caption: "A folder tree measured, largest files listed, and an honest note when part of the tree could not be read.",
    alt: "The WinSweep Analyze page showing folder sizes and the largest files for a mapped directory."
  }
};

const screen = document.getElementById("screen");
const panel = document.getElementById("screen-panel");
const image = document.getElementById("screen-image");
const title = document.getElementById("screen-title");
const caption = document.getElementById("screen-caption");
const tabs = Array.from(document.querySelectorAll(".screen-tabs button"));
const wantsMotion = !window.matchMedia("(prefers-reduced-motion: reduce)").matches;

// Warm the cache so switching pages does not flash an empty frame.
Object.keys(PAGES).forEach((key) => {
  const preload = new Image();
  preload.src = `assets/screenshots/${key}.png`;
});

// The selected tab is the only one in the tab order; the arrow keys move within.
function select(tab, group) {
  group.forEach((other) => {
    const active = other === tab;
    other.setAttribute("aria-selected", String(active));
    other.tabIndex = active ? 0 : -1;
  });
}

function store(key, value) {
  const url = new URL(window.location.href);
  url.searchParams.set(key, value);
  window.history.replaceState(null, "", url);
}

function showPage(key, updateUrl = true) {
  const page = PAGES[key];
  if (!page) return;

  const tab = tabs.find((entry) => entry.dataset.page === key);
  if (!tab) return;

  select(tab, tabs);
  screen.dataset.page = key;
  screen.style.setProperty("--ambient", page.ambient);
  panel.setAttribute("aria-labelledby", tab.id);

  const commit = () => {
    image.src = `assets/screenshots/${key}.png`;
    image.alt = page.alt;
    title.textContent = page.title;
    caption.textContent = page.caption;
    image.classList.remove("is-swapping");
  };

  if (wantsMotion) {
    image.classList.add("is-swapping");
    window.setTimeout(commit, 130);
  } else {
    commit();
  }

  if (updateUrl) store("page", key);
}

function wireTablist(group, onSelect) {
  group.forEach((tab, index) => {
    tab.addEventListener("click", () => onSelect(tab));

    tab.addEventListener("keydown", (event) => {
      const keys = {
        ArrowRight: (index + 1) % group.length,
        ArrowLeft: (index - 1 + group.length) % group.length,
        Home: 0,
        End: group.length - 1
      };
      const next = keys[event.key];
      if (next === undefined) return;

      event.preventDefault();
      const target = group[next];
      target.focus();
      onSelect(target);
    });
  });
}

wireTablist(tabs, (tab) => showPage(tab.dataset.page));

// Cleanup categories, transcribed from WinSweep.Core/Cleanup/CleanupCatalog.cs.
const CLEANUP_RULES = {
  System: [
    { name: "Temporary files", age: "1 day" },
    { name: "Application crash dumps", age: "1 day" },
    { name: "Internet cache", age: "1 day" },
    { name: "Windows error reports", age: "7 days" },
    { name: "Thumbnail cache", age: "1 day" },
    { name: "Windows temp folder", age: "7 days", admin: true }
  ],
  Browsers: [
    { name: "Chrome cache, code cache, GPU cache", age: "1 day" },
    { name: "Edge cache, code cache, GPU cache", age: "1 day" },
    { name: "Firefox cache", age: "1 day" }
  ],
  Graphics: [
    { name: "DirectX shader cache", age: "7 days" },
    { name: "NVIDIA shader cache", age: "7 days" },
    { name: "NVIDIA OpenGL cache", age: "7 days" }
  ],
  "Developer tools": [
    { name: "Visual Studio Code cache", age: "1 day" },
    { name: "Visual Studio Code cached data", age: "1 day" },
    { name: "npm cache", age: "7 days" },
    { name: "pip cache", age: "7 days" },
    { name: "NuGet package cache", age: "30 days" }
  ]
};

const ruleList = document.getElementById("rule-list");
const categoryButtons = Array.from(document.querySelectorAll(".segmented button"));

function renderCategory(button, updateUrl = true) {
  const rules = CLEANUP_RULES[button.dataset.category];
  if (!rules || !ruleList) return;

  select(button, categoryButtons);
  ruleList.setAttribute("aria-labelledby", button.id);

  ruleList.replaceChildren(
    ...rules.map((rule) => {
      const row = document.createElement("li");

      const name = document.createElement("span");
      name.className = "rule-name";
      name.textContent = rule.name;

      const meta = document.createElement("span");
      meta.className = "rule-meta";
      meta.append(document.createTextNode(`min age ${rule.age}`));
      if (rule.admin) {
        const admin = document.createElement("span");
        admin.className = "admin";
        admin.textContent = " / administrator";
        meta.append(admin);
      }

      row.append(name, meta);
      return row;
    })
  );

  if (updateUrl) store("category", button.dataset.category.toLowerCase().replace(/\s+/g, "-"));
}

wireTablist(categoryButtons, (button) => renderCategory(button));

// Restore whatever the link asked for, so a shared URL opens on the same page.
const params = new URLSearchParams(window.location.search);
const wantedPage = params.get("page");
if (wantedPage && PAGES[wantedPage]) {
  showPage(wantedPage, false);
} else {
  screen.style.setProperty("--ambient", PAGES.status.ambient);
}

const wantedCategory = params.get("category");
const initialCategory = categoryButtons.find(
  (button) => button.dataset.category.toLowerCase().replace(/\s+/g, "-") === wantedCategory
);
renderCategory(initialCategory ?? categoryButtons[0], false);

// Copy the build commands from the download card.
const copyButton = document.querySelector(".copy");
if (copyButton) {
  copyButton.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(copyButton.dataset.copy);
      copyButton.textContent = "Copied";
      window.setTimeout(() => { copyButton.textContent = "Copy"; }, 2000);
    } catch {
      copyButton.textContent = "Select the text instead";
      window.setTimeout(() => { copyButton.textContent = "Copy"; }, 2500);
    }
  });
}
