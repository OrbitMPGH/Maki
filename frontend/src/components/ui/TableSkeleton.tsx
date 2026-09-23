import { Skeleton, Table } from '@mantine/core'
import { Panel } from './Panel'

const WIDTHS = ['72%', '48%', '60%', '38%', '54%', '66%']

/** A ruled table's outline while its rows load. Same panel and row height as the real one, so nothing moves when data lands. */
export function TableSkeleton({ columns, rows = 6 }: { columns: number; rows?: number }) {
  return (
    <Panel p={0} className="table-panel" aria-hidden>
      <Table className="panel-table" verticalSpacing="sm">
        <Table.Thead>
          <Table.Tr>
            {Array.from({ length: columns }, (_, c) => (
              <Table.Th key={c}>
                <Skeleton h={8} w={c === 0 ? 64 : 44} />
              </Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {Array.from({ length: rows }, (_, r) => (
            <Table.Tr key={r}>
              {Array.from({ length: columns }, (_, c) => (
                <Table.Td key={c}>
                  <Skeleton h={10} w={WIDTHS[(r + c * 2) % WIDTHS.length]} />
                </Table.Td>
              ))}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Panel>
  )
}
