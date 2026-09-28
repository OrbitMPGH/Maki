/** Whether a comic file name is a PDF, read in place rather than a CBZ. */
export function isPdfFile(fileName: string): boolean {
  return fileName.toLowerCase().endsWith('.pdf')
}
