'use client';
import { useStaticSearch } from 'fumadocs-core/search/client';
import {
  SearchDialog,
  SearchDialogClose,
  SearchDialogContent,
  SearchDialogHeader,
  SearchDialogIcon,
  SearchDialogInput,
  SearchDialogList,
  SearchDialogOverlay,
  type SharedProps,
} from 'fumadocs-ui/components/dialog/search';
import { useState } from 'react';
import { searchIndexRoute } from '@/lib/shared';

// Search runs in the browser over the static index the export holds; there is no search server.
export default function StaticSearchDialog(props: SharedProps) {
  const search = useStaticSearch({ from: searchIndexRoute });
  // The first search downloads the index, and the dialog is blank until it lands. isLoading is also true for a moment on
  // every later query, so the row stops at the first results.
  const [indexLoaded, setIndexLoaded] = useState(false);
  if (search.data && !indexLoaded) setIndexLoaded(true);

  return (
    <SearchDialog {...search} {...props}>
      <SearchDialogOverlay />
      <SearchDialogContent>
        <SearchDialogHeader>
          <SearchDialogIcon />
          <SearchDialogInput />
          <SearchDialogClose />
        </SearchDialogHeader>
        {search.isLoading && !indexLoaded && (
          <p role="status" className="p-3 text-sm text-fd-muted-foreground">
            Loading search index…
          </p>
        )}
        <SearchDialogList />
      </SearchDialogContent>
    </SearchDialog>
  );
}
