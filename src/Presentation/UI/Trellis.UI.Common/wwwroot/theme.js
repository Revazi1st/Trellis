/* The theme switch's half in the browser (docs/design-system.md, Themes and the switch): the user's
   choice is kept in localStorage under "theme" and written as data-theme on <html>, which tokens.css
   puts above the operating system's scheme. App.razor loads this file in <head>, before the stylesheets
   and without defer, so the attribute is set before the first paint and a kept Dark never flashes light.
   A file, never an inline script: the host's Content-Security-Policy is script-src 'self'.
   window.appTheme is its one global, read and written by Layout/ThemeToggle.razor. */
(() => {
  const key = "theme";
  const root = document.documentElement;
  const named = (value) => (value === "light" || value === "dark" ? value : null);

  const kept = () => {
    try {
      return named(localStorage.getItem(key));
    } catch {
      return null; // storage refused (a private window, blocked site data): follow the system
    }
  };

  const show = (theme) => {
    if (theme) root.dataset.theme = theme;
    else delete root.dataset.theme;
  };

  show(kept());

  // An enhanced navigation (a link out of the static /Error page) rewrites <html>'s attributes: put the choice back.
  document.addEventListener("DOMContentLoaded", () => window.Blazor?.addEventListener?.("enhancedload", () => show(kept())));

  window.appTheme = {
    get: () => named(root.dataset.theme) ?? "system",
    set: (value) => {
      const theme = named(value);
      try {
        if (theme) localStorage.setItem(key, theme);
        else localStorage.removeItem(key);
      } catch {
        // not kept, but shown on this page all the same
      }
      show(theme);
    },
  };
})();
