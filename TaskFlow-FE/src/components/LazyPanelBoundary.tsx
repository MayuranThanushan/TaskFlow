import { Component } from 'react'
import type { ReactNode } from 'react'
type Props={children:ReactNode;title:string;message:string;reloadLabel:string;headingLevel?:1|2;className?:string;fallbackLabel?:string;onFallback?:()=>void}
// Deployment tabs may reference a missing chunk. Recovery is explicit and never retries writes.
export class LazyPanelBoundary extends Component<Props,{failed:boolean}>{
  state={failed:false}
  static getDerivedStateFromError(){return {failed:true}}
  render(){const Heading=this.props.headingLevel===1?'h1':'h2';return this.state.failed?<section className={this.props.className??'projects-page'}><Heading>{this.props.title}</Heading><p role="alert">{this.props.message}</p><div className="form-actions"><button className="button subtle" onClick={()=>window.location.reload()}>{this.props.reloadLabel}</button>{this.props.onFallback&&<button className="button subtle" onClick={this.props.onFallback}>{this.props.fallbackLabel}</button>}</div></section>:this.props.children}
}
