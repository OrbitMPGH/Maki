/**
 * Text matcher for a MonoValue: it draws its value as a head and a tail span so it can lose its
 * middle, so a plain `findByText` with the whole value finds neither half.
 */
export const monoText =
  (value: string | RegExp) =>
  (_: string, el: Element | null): boolean => {
    if (!el?.classList.contains('mono-value-text')) return false
    const text = el.textContent ?? ''
    return typeof value === 'string' ? text === value : value.test(text)
  }
