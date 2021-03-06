import React, { Component } from 'react';
import { Route } from 'react-router';
import { Layout } from './components/Layout';
import { Home } from './components/Home';
import { FetchData } from './components/FetchData';
import { Counter } from './components/Counter';

import './custom.css'
import { RoomMenu } from './components/RoomMenu';
import { Chat } from './components/Chat';

export default class App extends Component {
  static displayName = App.name;

  render () {
    return (
      <Layout>
        <Route exact path='/' component={Home} />
        <Route path='/rooms' component={RoomMenu} />
        <Route path='/room/:id' component={Chat} />
      </Layout>
    );
  }
}
