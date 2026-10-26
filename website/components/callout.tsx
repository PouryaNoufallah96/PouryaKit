import { CircleCheck, CircleX, Info, Lightbulb, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';

const icons = { info: Info, warning: TriangleAlert, error: CircleX, success: CircleCheck, idea: Lightbulb };

type CalloutType = keyof typeof icons | 'warn' | 'tip';

// The site's callout: Fumadocs' types and colours on the card's shape, a 2px muted border with no side stripe. It
// replaces Fumadocs' Callout in MDX.
export function Callout({
  type = 'info',
  title,
  children,
}: {
  type?: CalloutType;
  title?: ReactNode;
  children?: ReactNode;
}) {
  const kind = type === 'warn' ? 'warning' : type === 'tip' ? 'info' : type;
  const Icon = icons[kind];

  return (
    <div
      role="note"
      className="my-4 flex gap-3 rounded-xl border-2 border-muted bg-card p-4 text-card-foreground text-sm dark:border-foreground/10"
    >
      <Icon aria-hidden="true" className="mt-0.5 size-4 shrink-0" style={{ color: `var(--color-fd-${kind})` }} />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        {title ? <p className="my-0! font-medium">{title}</p> : null}
        <div className="prose-no-margin text-foreground-secondary empty:hidden">{children}</div>
      </div>
    </div>
  );
}
