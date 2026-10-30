import Link from 'fumadocs-core/link';
import type { HTMLAttributes, ReactNode } from 'react';

// On HTMLElement, since the rest of these props land on a link or a div.
type CardProps = Omit<HTMLAttributes<HTMLElement>, 'title'> & {
  title: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  href?: string;
};

// The site's card: rounded-xl with a 2px muted border and no shadow at rest; a link card tints on hover. It replaces
// Fumadocs' Card in MDX and sits in Fumadocs' Cards grid.
export function Card({ title, description, icon, href, className, children, ...props }: CardProps) {
  const body = (
    <>
      {icon ? (
        <div className="not-prose mb-2 w-fit rounded-lg border bg-muted p-1.5 text-muted-foreground [&_svg]:size-4">
          {icon}
        </div>
      ) : null}
      <h3 className="not-prose mb-1 font-medium text-sm">{title}</h3>
      {description ? <p className="my-0! text-foreground-secondary text-sm">{description}</p> : null}
      <div className="prose-no-margin text-foreground-secondary text-sm empty:hidden">{children}</div>
    </>
  );
  const classes = `block rounded-xl border-2 border-muted bg-card p-4 text-card-foreground transition-colors @max-lg:col-span-full dark:border-foreground/10 ${className ?? ''}`;

  return href ? (
    <Link href={href} data-card="" className={`${classes} hover:bg-accent/80`} {...props}>
      {body}
    </Link>
  ) : (
    <div data-card="" className={classes} {...props}>
      {body}
    </div>
  );
}
