import './App.css'

function App() {
  return (
    <div className="app-shell">
      <header className="app-header">
        <a className="brand" href="/" aria-label="TaskFlow home">TaskFlow</a>
      </header>
      <main id="main-content" className="welcome">
        <p className="eyebrow">A clearer way to work</p>
        <h1>Keep your work moving.</h1>
        <p className="intro">One place for your projects, tasks, and team progress.</p>
        <p className="availability">TaskFlow is being built. Check back soon.</p>
      </main>
      <footer className="app-footer">TaskFlow / Project and task management</footer>
    </div>
  )
}

export default App
