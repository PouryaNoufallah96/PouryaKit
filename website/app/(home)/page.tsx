import { ArrowRight, Bot, Package } from 'lucide-react';
import Link from 'next/link';
import type { ReactNode } from 'react';
import { Badge } from '@/components/badge';
import { Card } from '@/components/card';
import { CommandBlock, KitSnippet, templatesInstall } from '@/components/kit-code';
import { findRoot } from '@/lib/kit-sources/paths.ts';
import { skillEntries } from '@/lib/kit-sources/references.ts';
import { docsRoute, siteDescription, siteName } from '@/lib/shared';

const FEATURE = 'samples/ModularMonolith/src/Modules/Catalog/Catalog/Products/Features/RenameProduct';

// Each card names a rule the Kit holds and the test class that proves it; the Capability page it links states the rule.
const guarantees = [
  {
    title: 'Feature code never saves',
    body: "A structure rule: the sample's tests hold its Modules to it, and a project copies them for its own.",
    proof: 'FeatureCodeTests',
    href: `${docsRoute}/capabilities/testing#structure-tests`,
  },
  {
    title: 'Notifications after the commit',
    body: 'A reaction that reaches outside the Module runs only once the change is committed.',
    proof: 'NotificationTests',
    href: `${docsRoute}/capabilities/persistence#what-the-kit-guarantees`,
  },
  {
    title: 'One problem response',
    body: 'Every failure answers the same problem response, with each Error of its Result.',
    proof: 'WebTests',
    href: `${docsRoute}/capabilities/web#problem-responses`,
  },
  {
    title: 'No default Actor',
    body: 'An Operation runs with the Operation Context its caller passes, or not at all.',
    proof: 'OperationContextTests',
    href: `${docsRoute}/capabilities/modules#the-operation-context`,
  },
  {
    title: 'Tenant isolation',
    body: "A tenant-scoped Module reads and writes only its Operation's tenant's data.",
    proof: 'TenancyTests',
    href: `${docsRoute}/capabilities/multi-tenancy#what-the-kit-guarantees`,
  },
  {
    title: 'Checked when the Host is built',
    body: 'A declaration that cannot work stops the Host before it serves a request.',
    proof: 'CompositionCheckTests',
    href: `${docsRoute}/capabilities/modules#the-hosts-checks`,
  },
];

const goldenPath = [
  {
    title: 'Run your first Host',
    body: 'Install the templates, generate a solution and call it.',
    href: `${docsRoute}/get-started/first-host`,
  },
  {
    title: 'Learn the Concepts',
    body: 'Host and Module, Operation and Result, the Unit of Work and its commit.',
    href: `${docsRoute}/concepts`,
  },
  {
    title: 'Build with the Guides',
    body: 'A command, a paged query, an endpoint, an Integration Event, a test.',
    href: `${docsRoute}/guides`,
  },
];

// A glossary term's first use on the page, linked to its anchor.
function Term({ anchor, children }: { anchor: string; children: ReactNode }) {
  return (
    <Link
      href={`${docsRoute}/reference/glossary#${anchor}`}
      className="text-foreground underline decoration-primary/50 underline-offset-4 hover:decoration-primary"
    >
      {children}
    </Link>
  );
}

function Section({ title, lead, children }: { title: string; lead?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-6">
      <div className="flex flex-col gap-2">
        <h2 className="font-semibold text-2xl tracking-tight">{title}</h2>
        {lead ? <p className="max-w-3xl text-foreground-secondary">{lead}</p> : null}
      </div>
      {children}
    </section>
  );
}

export default function HomePage() {
  const install = templatesInstall();
  const capabilities = skillEntries(findRoot()).filter((entry) => entry.group === 'Capabilities');

  return (
    <main className="mx-auto flex w-full max-w-5xl flex-1 flex-col gap-20 px-4 py-16 md:py-24">
      <section className="flex flex-col items-start gap-6">
        <div className="flex flex-wrap gap-2">
          <Badge label=".NET" value="10" />
          <Badge label="nuget" value={install.version} />
          <Badge label="licence" value="MIT" />
        </div>
        <h1 className="font-semibold text-4xl tracking-tight md:text-5xl">{siteName}</h1>
        <p className="max-w-2xl text-foreground-secondary text-lg">{siteDescription}</p>
        <p className="max-w-2xl text-foreground-secondary">
          Each <Term anchor="feature">Feature</Term> of a <Term anchor="module">Module</Term> supplies its command or
          query, its handler and its endpoint. The Kit runs the <Term anchor="operation">Operation</Term> with the
          caller's <Term anchor="operation-context">Operation Context</Term> and <Term anchor="tenant">tenant</Term>,
          owns its one commit in the <Term anchor="unit-of-work">Unit of Work</Term>, writes its{' '}
          <Term anchor="outbox">Outbox</Term> and delivers what it published, in any <Term anchor="host">Host</Term>{' '}
          that composes its <Term anchor="capability">Capabilities</Term>.
        </p>
        <div className="flex flex-wrap gap-3">
          <Link
            href={`${docsRoute}/get-started/prerequisites`}
            className="rounded-md bg-primary px-4 py-2 font-medium text-primary-foreground text-sm transition-colors hover:bg-primary/90"
          >
            Get started
          </Link>
          <Link
            href={`${docsRoute}/capabilities`}
            className="rounded-md border px-4 py-2 font-medium text-sm transition-colors hover:bg-accent"
          >
            Browse Capabilities
          </Link>
        </div>
        <div className="w-full max-w-2xl">
          <CommandBlock code={install.command} />
        </div>
      </section>

      <Section
        title="A Feature holds only business behaviour"
        lead={
          <>
            Renaming a product in the <Term anchor="proving-sample">Proving Sample</Term>: the command names the cache
            entry it makes stale, and the handler returns a <Term anchor="result">Result</Term>. Nothing here saves,
            commits or sends; the Kit runs the Operation around it.{' '}
            <Link href={`${docsRoute}/proving-sample`} className="font-medium text-primary hover:underline">
              Walk through the Proving Sample
            </Link>
            .
          </>
        }
      >
        <div className="grid gap-4">
          <KitSnippet file={`${FEATURE}/RenameProductCommand.cs`} region="rename-product-command" />
          <KitSnippet file={`${FEATURE}/RenameProductCommandHandler.cs`} region="rename-product-handler" />
        </div>
      </Section>

      <Section
        title="What the Kit guarantees"
        lead="The Proving Sample's tests prove each rule below, in the test class its card names, and the page it links states it in full. The first is a structure rule a project copies into its own tests."
      >
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {guarantees.map((guarantee) => (
            <Card key={guarantee.proof} title={guarantee.title} href={guarantee.href} description={guarantee.body}>
              <p className="mt-3 font-mono text-xs">Proof: {guarantee.proof}</p>
            </Card>
          ))}
        </div>
      </Section>

      <Section
        title="Capabilities"
        lead="Each Capability is one or a few packages, used on their own or together. Its page explains it and carries its reference."
      >
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {capabilities.map((entry) => (
            <Card
              key={entry.slug}
              title={entry.name}
              href={`${docsRoute}/capabilities/${entry.slug}`}
              description={entry.packages.join(', ')}
            />
          ))}
        </div>
      </Section>

      <Section title="The path through the docs">
        <ol className="grid gap-4 md:grid-cols-3">
          {goldenPath.map((step, index) => (
            <li key={step.href}>
              <Card
                className="h-full"
                href={step.href}
                title={
                  <span className="flex items-center gap-2">
                    <span className="flex size-6 items-center justify-center rounded-full bg-primary font-mono text-primary-foreground text-xs">
                      {index + 1}
                    </span>
                    {step.title}
                  </span>
                }
                description={step.body}
              />
            </li>
          ))}
        </ol>
      </Section>

      <section className="grid gap-4 md:grid-cols-2">
        <Card icon={<Bot />} title="Install the AI skill">
          <p>
            The Kit's skill teaches a coding agent the same rules these pages do. Add it to Claude Code, Codex and other
            agents:
          </p>
          <div className="not-prose mt-3">
            <CommandBlock code="npx skills add PouryaNoufallah96/pouryakit-public" />
          </div>
          <Link
            href={`${docsRoute}/get-started/ai-skill`}
            className="mt-3 inline-flex items-center gap-1 font-medium text-primary text-sm hover:underline"
          >
            Every way to install it <ArrowRight className="size-3.5" />
          </Link>
        </Card>
        <Card icon={<Package />} title="Packages on nuget.org">
          <p>
            Every package is published on nuget.org at {install.version}. The templates' package pins that version in
            each solution it generates.
          </p>
          <div className="mt-3 flex flex-col gap-1">
            <a
              href="https://www.nuget.org/packages/PouryaKit.Templates"
              className="inline-flex items-center gap-1 font-medium text-primary text-sm hover:underline"
            >
              PouryaKit.Templates on nuget.org <ArrowRight className="size-3.5" />
            </a>
            <Link
              href={`${docsRoute}/reference/packages`}
              className="inline-flex items-center gap-1 font-medium text-primary text-sm hover:underline"
            >
              Every package <ArrowRight className="size-3.5" />
            </Link>
          </div>
        </Card>
      </section>
    </main>
  );
}
