'use client';
import { RootProvider } from 'fumadocs-ui/provider/next';
import type { ReactNode } from 'react';
import StaticSearchDialog from '@/components/search';

// Mod+Shift+D toggles light and dark (Fumadocs ignores it while an input is focused).
function isThemeHotKey(event: KeyboardEvent) {
  return (event.metaKey || event.ctrlKey) && event.shiftKey && !event.altKey && event.key.toLowerCase() === 'd';
}

export function Provider({ children }: { children: ReactNode }) {
  return (
    <RootProvider search={{ SearchDialog: StaticSearchDialog }} theme={{ hotKey: isThemeHotKey }}>
      {children}
    </RootProvider>
  );
}
