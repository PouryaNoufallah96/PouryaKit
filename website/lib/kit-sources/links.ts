import path from 'node:path';
import type { Nodes, Parent, Root } from 'mdast';
import { visit } from 'unist-util-visit';
import { privateRepositoryReferences } from './rules.ts';

/**
 * A page the site renders from a source, keyed by the source's repository path. `anchors` maps each anchor a link to the
 * source may name to the anchor on the page, or to null when its section is not published; a page without it keeps
 * whatever anchor a link names.
 */
export type PageRoute = { route: string; anchors?: Map<string, string | null> };
export type RouteMap = Map<string, PageRoute>;

function unwrap(parent: Parent, index: number, node: Parent) {
  parent.children.splice(index, 1, ...(node.children as never[]));
}

/** Where a link written in the source `from` points: a repository path and an anchor, or nothing for a site route or another site. */
export function resolveLink(from: string, url: string): { target: string; anchor?: string } | undefined {
  // Another site, or a route of this one.
  if (/^[a-z][a-z0-9+.-]*:/i.test(url) || url.startsWith('/')) return undefined;
  const hash = url.indexOf('#');
  const file = hash === -1 ? url : url.slice(0, hash);
  const anchor = hash === -1 ? undefined : decodeURIComponent(url.slice(hash + 1));
  const target = file === '' ? from : path.posix.normalize(path.posix.join(path.posix.dirname(from), file));
  return { target: target.replace(/\/$/, ''), anchor };
}

/**
 * Rewrites every link of a projected source: a link to a source the site renders becomes that page's route, with its
 * anchor carried over; a link into the repository with no page becomes its own text, so a page never holds a dead
 * relative link. A link to another site stays. A link to the private repository, and an anchor the target page does not
 * have, fail. With `at`, the path the page is written to, each route is a path of the same tree, and a link is written
 * relative to `at`, as the public repository's copy of the skill links between its files.
 */
export function rewriteLinks(tree: Root, from: string, routes: RouteMap, at?: string): void {
  visit(tree, (node: Nodes, index, parent) => {
    if (node.type === 'linkReference' || node.type === 'definition') {
      throw new Error(`kit-sources: ${from} uses a reference-style link; write it inline`);
    }
    if (node.type !== 'link' || !parent || index === undefined) return;
    if (privateRepositoryReferences(node.url).length > 0) {
      throw new Error(`kit-sources: ${from} links the private repository: ${node.url}`);
    }
    const resolved = resolveLink(from, node.url);
    if (!resolved) return;
    const page = routes.get(resolved.target) ?? routes.get(`${resolved.target}/README.md`);
    if (!page || resolved.target.startsWith('..')) {
      unwrap(parent, index, node);
      return index;
    }
    let anchor = resolved.anchor;
    if (anchor !== undefined && page.anchors) {
      if (!page.anchors.has(anchor)) {
        throw new Error(`kit-sources: ${from} links ${node.url}, an anchor ${resolved.target} does not have`);
      }
      anchor = page.anchors.get(anchor) ?? undefined;
    }
    const route = at === undefined ? page.route : path.posix.relative(path.posix.dirname(at), page.route);
    if (resolved.target === from && anchor) node.url = `#${anchor}`;
    else node.url = anchor ? `${route}#${anchor}` : route;
  });
}
