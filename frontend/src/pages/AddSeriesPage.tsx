import { useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { CatalogueBrowser } from '../components/CatalogueBrowser'
import { PageHeader } from '../components/ui/PageHeader'

export default function AddSeriesPage() {
  // The command palette sends the text you typed there here as ?q= when the library holds no match.
  const [searchParams] = useSearchParams()
  const { can } = useAuth()

  // Same catalogue, same results, same detail modal: only the verb changes. Someone without
  // AddSeries files a request an admin actions instead of adding the series themselves.
  const canAdd = can('AddSeries')

  return (
    <>
      <PageHeader
        title={canAdd ? 'Add series' : 'Request series'}
        description={
          canAdd
            ? 'Browse or search MangaBaka, pick a title, choose where it lives, and Maki handles the rest.'
            : 'Browse or search MangaBaka and ask an admin for a title. You can ask for a chapter range too.'
        }
      />

      {/* No `idle` node, so an empty box browses the catalogue by popularity rather than showing an
          empty state. Filtering to a genre and a tag and never typing anything is a legitimate way
          to use this page, and the one the old search box could not do at all. Shares the Discover
          view and filter preferences, since it is the same catalogue seen from another door. */}
      <CatalogueBrowser
        scope="discover"
        seededQuery={searchParams.get('q')}
        placeholder="Search by title, description, or by feel"
      />
    </>
  )
}
