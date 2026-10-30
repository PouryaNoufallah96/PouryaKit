import { HomeLayout } from 'fumadocs-ui/layouts/home';
import type { ReactNode } from 'react';
import { baseOptions } from '@/lib/layout.shared';
import { docsRoute } from '@/lib/shared';

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <HomeLayout {...baseOptions()} links={[{ text: 'Documentation', url: docsRoute, active: 'nested-url' }]}>
      {children}
    </HomeLayout>
  );
}
