export type ThemePreference = "system" | "light" | "dark";

const STORAGE_KEY = "autonet.preview.theme";

export function loadThemePreference(): ThemePreference {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === "light" || raw === "dark" || raw === "system" ? raw : "system";
  } catch {
    return "system";
  }
}

export function systemPrefersDark(): boolean {
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)").matches
    : false;
}

export function applyThemePreference(preference: ThemePreference): void {
  const dark = preference === "dark" || (preference === "system" && systemPrefersDark());
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}

export function saveThemePreference(preference: ThemePreference): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, preference);
  } catch {
    // 存储不可用时仍应用当前选择；不把主题偏好当成业务状态。
  }
  applyThemePreference(preference);
}
