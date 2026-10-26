import { Blocks, Boxes, ShieldCheck } from 'lucide-react';
import Link from 'next/link';
import { Badge } from '@/components/badge';
import { Card } from '@/components/card';
import { docsRoute, siteDescription, siteName } from '@/lib/shared';

// A placeholder landing that proves the route and the design; the real landing replaces it.
export default function HomePage() {
  return (
    <main className="mx-auto flex w-full max-w-5xl flex-1 flex-col gap-12 px-4 py-16 md:py-24">
      <section className="flex flex-col items-start gap-6">
        <div className="flex flex-wrap gap-2">
          <Badge label=".NET" value="10" />
          <Badge label="licence" value="MIT" />
        </div>
        <h1 className="font-semibold text-4xl tracking-tight md:text-5xl">{siteName}</h1>
        <p className="max-w-2xl text-foreground-secondary text-lg">{siteDescription}</p>
        <div className="flex flex-wrap gap-3">
          <Link
            href={docsRoute}
            className="rounded-md bg-primary px-4 py-2 font-medium text-primary-foreground text-sm transition-colors hover:bg-primary/90"
          >
            Get started
          </Link>
          <Link
            href={`${docsRoute}/how-it-works`}
            className="rounded-md border px-4 py-2 font-medium text-sm transition-colors hover:bg-accent"
          >
            How the Kit works
          </Link>
        </div>
      </section>
      <section className="grid gap-4 md:grid-cols-3">
        <Card icon={<Boxes />} title="One commit per Operation">
          The Unit of Work writes the Outbox, saves and commits, and sends what it wrote only after the commit.
        </Card>
        <Card icon={<Blocks />} title="Modules behind their Gateway">
          Anything outside a Module reaches it through its Gateway, and every Gateway call is one Operation in its own
          scope.
        </Card>
        <Card icon={<ShieldCheck />} title="Checked while the Host is composed">
          A composition check stops the Host when its container is built, and a startup check when it starts.
        </Card>
      </section>
    </main>
  );
}
