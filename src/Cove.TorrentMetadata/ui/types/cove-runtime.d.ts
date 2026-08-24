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
}
