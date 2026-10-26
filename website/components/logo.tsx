// The Kit's mark: a Host holding its Modules. The same shape as app/icon.svg, in the theme's accent.
export function Logo({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" aria-hidden="true" className={className}>
      <rect width="32" height="32" rx="7" className="fill-primary" />
      <rect x="7" y="7" width="8" height="8" rx="1.5" className="fill-primary-foreground" />
      <rect x="17" y="7" width="8" height="8" rx="1.5" className="fill-primary-foreground" opacity="0.7" />
      <rect x="7" y="17" width="18" height="8" rx="1.5" className="fill-primary-foreground" opacity="0.85" />
    </svg>
  );
}
