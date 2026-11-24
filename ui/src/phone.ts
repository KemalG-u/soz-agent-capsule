import { mount } from 'svelte'
import './app.css'
import Phone from './phone/Phone.svelte'

export default mount(Phone, { target: document.getElementById('app')! })
