/**
 * How a tag naming style is presented.
 *
 * Its own module rather than a constant in a component because two of them need it now: the settings
 * panel offers the choice, and the review dialog names the style in effect without offering to change
 * it. It imports no React and no `@cove/runtime/*`, so it is reachable from a test — the same
 * rule that put `review.ts` and `payload.ts` where they are.
 *
 * The values are the server's, defined by `TagNameStyler.Parse`/`Serialize`. This file only decides
 * how they read.
 */

export interface TagStyle {
  /** The wire value, as `TagNameStyler.Serialize` writes it. */
  readonly value: string;
  /** How the style is named on its own, short enough to sit inside a sentence. */
  readonly label: string;
  /** The same tag under this style, so the choice can be made by looking rather than by guessing. */
  readonly example: string;
}

export const TAG_STYLES: readonly TagStyle[] = [
  { value: "titlecase", label: "Title Case", example: "Big Red Barn" },
  { value: "spaced", label: "spaces", example: "big red barn" },
  { value: "dotted", label: "as the tracker spells it", example: "big.red.barn" },
];

/**
 * Names a style for display, falling back to the raw value.
 *
 * The fallback is the point: the style is whatever the server last stored, and a host that has learnt
 * a style this bundle has not would otherwise render an empty sentence — "New tags are named ." reads
 * as a bug in the dialog rather than as a bundle that is behind.
 */
export function tagStyleLabel(value: string): string {
  return TAG_STYLES.find((style) => style.value === value)?.label ?? value;
}
