import type { ThemePreference } from "../theme";

const OPTIONS: { value: ThemePreference; label: string }[] = [
  { value: "system", label: "跟随系统" },
  { value: "light", label: "浅色" },
  { value: "dark", label: "深色" },
];

export function ThemeToggle(props: {
  value: ThemePreference;
  onChange: (value: ThemePreference) => void;
}) {
  return (
    <div className="theme-toggle" role="group" aria-label="主题">
      {OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          className="theme-toggle__button"
          aria-pressed={props.value === option.value}
          onClick={() => props.onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}
