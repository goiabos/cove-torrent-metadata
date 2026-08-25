// Minimal ambient types for the host-provided runtime modules. Cove resolves these specifiers through
// an import map at load time; they are never bundled, so only their shapes are needed here.
declare module "@cove/runtime/react" {
  import * as React from "react";
  export = React;
}

declare module "@cove/runtime/api" {
  export interface ExtensionFetchOptions extends RequestInit {
    timeoutMs?: number | null;
  }
  export function extensionFetch(input: string, init?: ExtensionFetchOptions): Promise<Response>;
}

declare module "@cove/runtime/react-dom-client" {
  import type { ReactNode } from "react";
  export interface Root {
    render(children: ReactNode): void;
    unmount(): void;
  }
  export function createRoot(container: Element | DocumentFragment): Root;
}

declare module "@cove/runtime/components" {
  import type { ReactElement } from "react";
  /** Cove's shared confirmation dialog, so extension prompts match the rest of the app. */
  export function ConfirmDialog(props: {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    destructive?: boolean;
    isPending?: boolean;
    errorMessage?: string | null;
    onConfirm: () => void | Promise<void>;
    onCancel: () => void;
  }): ReactElement | null;

  /**
   * The host's own list filter. Only the two paging members are declared, because they are the only
   * ones `DetailListPagination` reads and this extension holds no `FindFilter` of its own — the batch
   * page filters in the browser over rows it already has.
   */
  export interface FindFilter {
    page?: number;
    /** `0` together with `allowInfinitePageSize` means "show everything". */
    perPage?: number;
  }

  /**
   * Cove's list pagination, so the batch page turns pages with the host's own control rather than a
   * second one that looks nearly like it. Renders nothing at one page or at an infinite page size, and
   * repairs an out-of-range page through `onFilterChange`.
   */
  export function DetailListPagination(props: {
    filter: FindFilter;
    onFilterChange: (filter: FindFilter) => void;
    totalCount: number;
    allowInfinitePageSize?: boolean;
    className?: string;
    ariaLabel?: string;
  }): ReactElement | null;
}
